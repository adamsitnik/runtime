// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;

/*
 * Win32FileStream supports different modes of accessing the disk - async mode
 * and sync mode.  They are two completely different codepaths in the
 * sync & async methods (i.e. Read/Write vs. ReadAsync/WriteAsync).  File
 * handles in NT can be opened in only sync or overlapped (async) mode,
 * and we have to deal with this pain.  Stream has implementations of
 * the sync methods in terms of the async ones, so we'll
 * call through to our base class to get those methods when necessary.
 *
 * Also buffering is added into Win32FileStream as well. Folded in the
 * code from BufferedStream, so all the comments about it being mostly
 * aggressive (and the possible perf improvement) apply to Win32FileStream as
 * well.  Also added some buffering to the async code paths.
 *
 * Class Invariants:
 * The class has one buffer, shared for reading & writing.  It can only be
 * used for one or the other at any point in time - not both.  The following
 * should be true:
 *   0 <= _readPos <= _readLen < _bufferSize
 *   0 <= _writePos < _bufferSize
 *   _readPos == _readLen && _readPos > 0 implies the read buffer is valid,
 *     but we're at the end of the buffer.
 *   _readPos == _readLen == 0 means the read buffer contains garbage.
 *   Either _writePos can be greater than 0, or _readLen & _readPos can be
 *     greater than zero, but neither can be greater than zero at the same time.
 *
 */

namespace System.IO
{
    internal abstract class WindowsFileStreamStrategy : FileStreamStrategy
    {
        // Error codes (not HRESULTS), from winerror.h
        internal const int ERROR_BROKEN_PIPE = 109;
        internal const int ERROR_NO_DATA = 232;
        protected const int ERROR_HANDLE_EOF = 38;
        protected const int ERROR_INVALID_PARAMETER = 87;
        protected const int ERROR_IO_PENDING = 997;

        protected readonly SafeFileHandle _fileHandle; // only ever null if ctor throws

        /// <summary>Whether the file is opened for reading, writing, or both.</summary>
        private readonly FileAccess _access;

        /// <summary>The path to the opened file.</summary>
        protected readonly string? _path;

        protected long _filePosition;

        private readonly bool _canSeek;
        private readonly bool _isPipe;      // Whether to disable async buffering code.

        /// <summary>Whether the file stream's handle has been exposed.</summary>
        protected bool _exposedHandle;

        private long _appendStart; // When appending, prevent overwriting file.

        internal WindowsFileStreamStrategy(SafeFileHandle handle, FileAccess access)
        {
            _exposedHandle = true;

            InitFromHandle(handle, access, out _canSeek, out _isPipe);

            // Note: Cleaner to set the following fields in ValidateAndInitFromHandle,
            // but we can't as they're readonly.
            _access = access;

            // As the handle was passed in, we must set the handle field at the very end to
            // avoid the finalizer closing the handle when we throw errors.
            _fileHandle = handle;
        }

        internal WindowsFileStreamStrategy(string path, FileMode mode, FileAccess access, FileShare share, FileOptions options)
        {
            string fullPath = Path.GetFullPath(path);

            _path = fullPath;
            _access = access;

            _fileHandle = FileStreamHelpers.OpenHandle(fullPath, mode, access, share, options);

            try
            {
                _canSeek = true;

                Init(mode, path);
            }
            catch
            {
                // If anything goes wrong while setting up the stream, make sure we deterministically dispose
                // of the opened handle.
                _fileHandle.Dispose();
                _fileHandle = null!;
                throw;
            }
        }

        public sealed override bool CanSeek => _canSeek;

        public sealed override bool CanRead => !_fileHandle.IsClosed && (_access & FileAccess.Read) != 0;

        public sealed override bool CanWrite => !_fileHandle.IsClosed && (_access & FileAccess.Write) != 0;

        public unsafe sealed override long Length
        {
            get
            {
                Interop.Kernel32.FILE_STANDARD_INFO info;

                if (!Interop.Kernel32.GetFileInformationByHandleEx(_fileHandle, Interop.Kernel32.FileStandardInfo, &info, (uint)sizeof(Interop.Kernel32.FILE_STANDARD_INFO)))
                {
                    throw Win32Marshal.GetExceptionForLastWin32Error(_path);
                }

                return info.EndOfFile;
            }
        }

        /// <summary>Gets or sets the position within the current stream</summary>
        public override long Position
        {
            get
            {
                VerifyOSHandlePosition();

                return _filePosition;
            }
            set
            {
                Seek(value, SeekOrigin.Begin);
            }
        }

        internal sealed override string Name => _path ?? SR.IO_UnknownFileName;

        internal sealed override bool IsClosed => _fileHandle.IsClosed;

        internal sealed override SafeFileHandle SafeFileHandle
        {
            get
            {
                // Flushing is the responsibility of BufferedFileStreamStrategy
                _exposedHandle = true;
                return _fileHandle;
            }
        }

        // this method just disposes everything as there is no buffer here
        // and we don't really need to Flush anything here
        public override ValueTask DisposeAsync()
        {
            if (_fileHandle != null && !_fileHandle.IsClosed)
            {
                _fileHandle.ThreadPoolBinding?.Dispose();
                _fileHandle.Dispose();
            }

            GC.SuppressFinalize(this); // the handle is closed; nothing further for the finalizer to do

            return ValueTask.CompletedTask;
        }

        // this method in the future will be called in no-buffering scenarios
        internal sealed override void DisposeInternal(bool disposing) => Dispose(disposing);

        // this method is called from BufferedStream.Dispose so the content is already flushed
        protected override void Dispose(bool disposing)
        {
            if (_fileHandle != null && !_fileHandle.IsClosed)
            {
                _fileHandle.ThreadPoolBinding?.Dispose();
                _fileHandle.Dispose();
            }

            // Don't set the buffer to null, to avoid a NullReferenceException
            // when users have a race condition in their code (i.e. they call
            // Close when calling another method on Stream like Read).
        }

        public sealed override void Flush() => Flush(flushToDisk: false); // we have nothing to flush as there is no buffer here

        internal sealed override void Flush(bool flushToDisk)
        {
            if (flushToDisk && CanWrite)
            {
                if (!Interop.Kernel32.FlushFileBuffers(_fileHandle))
                {
                    throw Win32Marshal.GetExceptionForLastWin32Error(_path);
                }
            }
        }

        public sealed override long Seek(long offset, SeekOrigin origin)
        {
            if (origin < SeekOrigin.Begin || origin > SeekOrigin.End)
                throw new ArgumentException(SR.Argument_InvalidSeekOrigin, nameof(origin));
            if (_fileHandle.IsClosed) throw Error.GetFileNotOpen();
            if (!CanSeek) throw Error.GetSeekNotSupported();

            // Verify that internal position is in sync with the handle
            VerifyOSHandlePosition();

            long oldPos = _filePosition;
            long pos = SeekCore(_fileHandle, offset, origin);

            // Prevent users from overwriting data in a file that was opened in
            // append mode.
            if (_appendStart != -1 && pos < _appendStart)
            {
                SeekCore(_fileHandle, oldPos, SeekOrigin.Begin);
                throw new IOException(SR.IO_SeekAppendOverwrite);
            }

            return pos;
        }

        // This doesn't do argument checking.  Necessary for SetLength, which must
        // set the file pointer beyond the end of the file. This will update the
        // internal position
        protected long SeekCore(SafeFileHandle fileHandle, long offset, SeekOrigin origin, bool closeInvalidHandle = false)
        {
            Debug.Assert(!fileHandle.IsClosed && _canSeek, "!fileHandle.IsClosed && _canSeek");
            Debug.Assert(origin >= SeekOrigin.Begin && origin <= SeekOrigin.End, "origin >= SeekOrigin.Begin && origin <= SeekOrigin.End");

            if (!Interop.Kernel32.SetFilePointerEx(fileHandle, offset, out long ret, (uint)origin))
            {
                if (closeInvalidHandle)
                {
                    throw Win32Marshal.GetExceptionForWin32Error(GetLastWin32ErrorAndDisposeHandleIfInvalid(), _path);
                }
                else
                {
                    throw Win32Marshal.GetExceptionForLastWin32Error(_path);
                }
            }

            _filePosition = ret;
            return ret;
        }

        internal sealed override void Lock(long position, long length)
        {
            int positionLow = unchecked((int)(position));
            int positionHigh = unchecked((int)(position >> 32));
            int lengthLow = unchecked((int)(length));
            int lengthHigh = unchecked((int)(length >> 32));

            if (!Interop.Kernel32.LockFile(_fileHandle, positionLow, positionHigh, lengthLow, lengthHigh))
            {
                throw Win32Marshal.GetExceptionForLastWin32Error(_path);
            }
        }

        internal sealed override void Unlock(long position, long length)
        {
            int positionLow = unchecked((int)(position));
            int positionHigh = unchecked((int)(position >> 32));
            int lengthLow = unchecked((int)(length));
            int lengthHigh = unchecked((int)(length >> 32));

            if (!Interop.Kernel32.UnlockFile(_fileHandle, positionLow, positionHigh, lengthLow, lengthHigh))
            {
                throw Win32Marshal.GetExceptionForLastWin32Error(_path);
            }
        }

        protected abstract void OnInitFromHandle(SafeFileHandle handle);

        protected virtual void OnInit() { }

        private void Init(FileMode mode, string originalPath)
        {
            if (!PathInternal.IsExtended(originalPath))
            {
                // To help avoid stumbling into opening COM/LPT ports by accident, we will block on non file handles unless
                // we were explicitly passed a path that has \\?\. GetFullPath() will turn paths like C:\foo\con.txt into
                // \\.\CON, so we'll only allow the \\?\ syntax.

                int fileType = Interop.Kernel32.GetFileType(_fileHandle);
                if (fileType != Interop.Kernel32.FileTypes.FILE_TYPE_DISK)
                {
                    int errorCode = fileType == Interop.Kernel32.FileTypes.FILE_TYPE_UNKNOWN
                        ? Marshal.GetLastWin32Error()
                        : Interop.Errors.ERROR_SUCCESS;

                    _fileHandle.Dispose();

                    if (errorCode != Interop.Errors.ERROR_SUCCESS)
                    {
                        throw Win32Marshal.GetExceptionForWin32Error(errorCode);
                    }
                    throw new NotSupportedException(SR.NotSupported_FileStreamOnNonFiles);
                }
            }

            OnInit();

            // For Append mode...
            if (mode == FileMode.Append)
            {
                _appendStart = SeekCore(_fileHandle, 0, SeekOrigin.End);
            }
            else
            {
                _appendStart = -1;
            }
        }

        private void InitFromHandle(SafeFileHandle handle, FileAccess access, out bool canSeek, out bool isPipe)
        {
#if DEBUG
            bool hadBinding = handle.ThreadPoolBinding != null;

            try
            {
#endif
                InitFromHandleImpl(handle, out canSeek, out isPipe);
#if DEBUG
            }
            catch
            {
                Debug.Assert(hadBinding || handle.ThreadPoolBinding == null, "We should never error out with a ThreadPoolBinding we've added");
                throw;
            }
#endif
        }

        private void InitFromHandleImpl(SafeFileHandle handle, out bool canSeek, out bool isPipe)
        {
            int handleType = Interop.Kernel32.GetFileType(handle);
            Debug.Assert(handleType == Interop.Kernel32.FileTypes.FILE_TYPE_DISK || handleType == Interop.Kernel32.FileTypes.FILE_TYPE_PIPE || handleType == Interop.Kernel32.FileTypes.FILE_TYPE_CHAR, "FileStream was passed an unknown file type!");

            canSeek = handleType == Interop.Kernel32.FileTypes.FILE_TYPE_DISK;
            isPipe = handleType == Interop.Kernel32.FileTypes.FILE_TYPE_PIPE;

            OnInitFromHandle(handle);

            if (_canSeek)
                SeekCore(handle, 0, SeekOrigin.Current);
            else
                _filePosition = 0;
        }

        public sealed override void SetLength(long value)
        {
            if (_appendStart != -1 && value < _appendStart)
                throw new IOException(SR.IO_SetLengthAppendTruncate);

            SetLengthCore(value);
        }

        // We absolutely need this method broken out so that WriteInternalCoreAsync can call
        // a method without having to go through buffering code that might call FlushWrite.
        protected unsafe void SetLengthCore(long value)
        {
            Debug.Assert(value >= 0, "value >= 0");
            VerifyOSHandlePosition();

            var eofInfo = new Interop.Kernel32.FILE_END_OF_FILE_INFO
            {
                EndOfFile = value
            };

            if (!Interop.Kernel32.SetFileInformationByHandle(
                _fileHandle,
                Interop.Kernel32.FileEndOfFileInfo,
                &eofInfo,
                (uint)sizeof(Interop.Kernel32.FILE_END_OF_FILE_INFO)))
            {
                int errorCode = Marshal.GetLastWin32Error();
                if (errorCode == Interop.Errors.ERROR_INVALID_PARAMETER)
                    throw new ArgumentOutOfRangeException(nameof(value), SR.ArgumentOutOfRange_FileLengthTooBig);
                throw Win32Marshal.GetExceptionForWin32Error(errorCode, _path);
            }

            if (_filePosition > value)
            {
                SeekCore(_fileHandle, 0, SeekOrigin.End);
            }
        }

        /// <summary>
        /// Verify that the actual position of the OS's handle equals what we expect it to.
        /// This will fail if someone else moved the UnixFileStream's handle or if
        /// our position updating code is incorrect.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void VerifyOSHandlePosition()
        {
            bool verifyPosition = _exposedHandle; // in release, only verify if we've given out the handle such that someone else could be manipulating it
#if DEBUG
            verifyPosition = true; // in debug, always make sure our position matches what the OS says it should be
#endif
            if (verifyPosition && CanSeek)
            {
                long oldPos = _filePosition; // SeekCore will override the current _position, so save it now
                long curPos = SeekCore(_fileHandle, 0, SeekOrigin.Current);
                if (oldPos != curPos)
                {
                    // For reads, this is non-fatal but we still could have returned corrupted
                    // data in some cases, so discard the internal buffer. For writes,
                    // this is a problem; discard the buffer and error out.

                    throw new IOException(SR.IO_FileStreamHandlePosition);
                }
            }
        }

        protected int GetLastWin32ErrorAndDisposeHandleIfInvalid()
        {
            int errorCode = Marshal.GetLastWin32Error();

            // If ERROR_INVALID_HANDLE is returned, it doesn't suffice to set
            // the handle as invalid; the handle must also be closed.
            //
            // Marking the handle as invalid but not closing the handle
            // resulted in exceptions during finalization and locked column
            // values (due to invalid but unclosed handle) in SQL Win32FileStream
            // scenarios.
            //
            // A more mainstream scenario involves accessing a file on a
            // network share. ERROR_INVALID_HANDLE may occur because the network
            // connection was dropped and the server closed the handle. However,
            // the client side handle is still open and even valid for certain
            // operations.
            //
            // Note that _parent.Dispose doesn't throw so we don't need to special case.
            // SetHandleAsInvalid only sets _closed field to true (without
            // actually closing handle) so we don't need to call that as well.
            if (errorCode == Interop.Errors.ERROR_INVALID_HANDLE)
            {
                _fileHandle.Dispose();
            }

            return errorCode;
        }

        // __ConsoleStream also uses this code.
        protected unsafe int ReadFileNative(SafeFileHandle handle, Span<byte> bytes, NativeOverlapped* overlapped, out int errorCode)
        {
            Debug.Assert(handle != null, "handle != null");

            int r;
            int numBytesRead = 0;

            fixed (byte* p = &MemoryMarshal.GetReference(bytes))
            {
                r = overlapped != null ?
                    Interop.Kernel32.ReadFile(handle, p, bytes.Length, IntPtr.Zero, overlapped) :
                    Interop.Kernel32.ReadFile(handle, p, bytes.Length, out numBytesRead, IntPtr.Zero);
            }

            if (r == 0)
            {
                errorCode = GetLastWin32ErrorAndDisposeHandleIfInvalid();
                return -1;
            }
            else
            {
                errorCode = 0;
                return numBytesRead;
            }
        }

        protected unsafe int WriteFileNative(SafeFileHandle handle, ReadOnlySpan<byte> buffer, NativeOverlapped* overlapped, out int errorCode)
        {
            Debug.Assert(handle != null, "handle != null");

            int numBytesWritten = 0;
            int r;

            fixed (byte* p = &MemoryMarshal.GetReference(buffer))
            {
                r = overlapped != null ?
                    Interop.Kernel32.WriteFile(handle, p, buffer.Length, IntPtr.Zero, overlapped) :
                    Interop.Kernel32.WriteFile(handle, p, buffer.Length, out numBytesWritten, IntPtr.Zero);
            }

            if (r == 0)
            {
                errorCode = GetLastWin32ErrorAndDisposeHandleIfInvalid();
                return -1;
            }
            else
            {
                errorCode = 0;
                return numBytesWritten;
            }
        }
    }
}
