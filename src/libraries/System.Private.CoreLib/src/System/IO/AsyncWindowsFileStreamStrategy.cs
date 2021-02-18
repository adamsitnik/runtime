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
    internal sealed partial class AsyncWindowsFileStreamStrategy : WindowsFileStreamStrategy
    {
        private static readonly unsafe IOCompletionCallback s_ioCallback = FileStreamCompletionSource.IOCallback;

        private PreAllocatedOverlapped? _preallocatedOverlapped;     // optimization for async ops to avoid per-op allocations
        private FileStreamCompletionSource? _currentOverlappedOwner; // async op currently using the preallocated overlapped

        internal AsyncWindowsFileStreamStrategy(SafeFileHandle handle, FileAccess access)
            : base(handle, access)
        {
        }

        internal AsyncWindowsFileStreamStrategy(string path, FileMode mode, FileAccess access, FileShare share, FileOptions options)
            : base(path, mode, access, share, options)
        {
        }

        internal override bool IsAsync => true;

        public override ValueTask DisposeAsync()
        {
            // the order matters, let the base class Dispose handle first
            ValueTask result = base.DisposeAsync();
            Debug.Assert(result.IsCompleted, "the method must be sync, as it performs no flushing");

            _preallocatedOverlapped?.Dispose();

            return result;
        }

        protected override void Dispose(bool disposing)
        {
            // the order matters, let the base class Dispose handle first
            base.Dispose(disposing);

            _preallocatedOverlapped?.Dispose();
        }

        protected override void OnInitFromHandle(SafeFileHandle handle)
        {
            // This is necessary for async IO using IO Completion ports via our
            // managed Threadpool API's.  This calls the OS's
            // BindIoCompletionCallback method, and passes in a stub for the
            // LPOVERLAPPED_COMPLETION_ROUTINE.  This stub looks at the Overlapped
            // struct for this request and gets a delegate to a managed callback
            // from there, which it then calls on a threadpool thread.  (We allocate
            // our native OVERLAPPED structs 2 pointers too large and store EE
            // state & a handle to a delegate there.)
            //
            // If, however, we've already bound this file handle to our completion port,
            // don't try to bind it again because it will fail.  A handle can only be
            // bound to a single completion port at a time.
            if (!(handle.IsAsync ?? false))
            {
                try
                {
                    handle.ThreadPoolBinding = ThreadPoolBoundHandle.BindHandle(handle);
                }
                catch (Exception ex)
                {
                    // If you passed in a synchronous handle and told us to use
                    // it asynchronously, throw here.
                    throw new ArgumentException(SR.Arg_HandleNotAsync, nameof(handle), ex);
                }
            }
        }

        protected override void OnInit()
        {
            // This is necessary for async IO using IO Completion ports via our
            // managed Threadpool API's.  This (theoretically) calls the OS's
            // BindIoCompletionCallback method, and passes in a stub for the
            // LPOVERLAPPED_COMPLETION_ROUTINE.  This stub looks at the Overlapped
            // struct for this request and gets a delegate to a managed callback
            // from there, which it then calls on a threadpool thread.  (We allocate
            // our native OVERLAPPED structs 2 pointers too large and store EE state
            // & GC handles there, one to an IAsyncResult, the other to a delegate.)
            try
            {
                _fileHandle.ThreadPoolBinding = ThreadPoolBoundHandle.BindHandle(_fileHandle);
            }
            catch (ArgumentException ex)
            {
                throw new IOException(SR.IO_BindHandleFailed, ex);
            }
            finally
            {
                if (_fileHandle.ThreadPoolBinding == null)
                {
                    // We should close the handle so that the handle is not open until SafeFileHandle GC
                    Debug.Assert(!_exposedHandle, "Are we closing handle that we exposed/not own, how?");
                    _fileHandle.Dispose();
                }
            }
        }

        // called by BufferedStream. TODO: find a cleaner solution
        internal void OnBufferAllocated(byte[] buffer)
        {
            Debug.Assert(buffer != null);
            Debug.Assert(_preallocatedOverlapped == null);

            _preallocatedOverlapped = new PreAllocatedOverlapped(s_ioCallback, this, buffer);
            _buffer = buffer;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsyncInternal(new Memory<byte>(buffer, offset, count)).GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsyncInternal(new Memory<byte>(buffer, offset, count), cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
            => new ValueTask<int>(ReadAsyncInternal(destination, cancellationToken));

        private unsafe Task<int> ReadAsyncInternal(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            Debug.Assert(CanRead, "BufferedStream has already verified that");
            Debug.Assert(!_fileHandle.IsClosed, "!_handle.IsClosed");

            // Create and store async stream class library specific data in the async result
            FileStreamCompletionSource completionSource = FileStreamCompletionSource.Create(this, 0, destination);
            NativeOverlapped* intOverlapped = completionSource.Overlapped;

            // Calculate position in the file we should be at after the read is done
            if (CanSeek)
            {
                long len = Length;

                // Make sure we are reading from the position that we think we are
                VerifyOSHandlePosition();

                if (_filePosition + destination.Length > len)
                {
                    if (_filePosition <= len)
                    {
                        destination = destination.Slice(0, (int)(len - _filePosition));
                    }
                    else
                    {
                        destination = default;
                    }
                }

                // Now set the position to read from in the NativeOverlapped struct
                // For pipes, we should leave the offset fields set to 0.
                intOverlapped->OffsetLow = unchecked((int)_filePosition);
                intOverlapped->OffsetHigh = (int)(_filePosition >> 32);

                // When using overlapped IO, the OS is not supposed to
                // touch the file pointer location at all.  We will adjust it
                // ourselves. This isn't threadsafe.

                // WriteFile should not update the file pointer when writing
                // in overlapped mode, according to MSDN.  But it does update
                // the file pointer when writing to a UNC path!
                // So changed the code below to seek to an absolute
                // location, not a relative one.  ReadFile seems consistent though.
                SeekCore(_fileHandle, destination.Length, SeekOrigin.Current);
            }

            // queue an async ReadFile operation and pass in a packed overlapped
            int r = ReadFileNative(_fileHandle, destination.Span, intOverlapped, out int errorCode);

            // ReadFile, the OS version, will return 0 on failure.  But
            // my ReadFileNative wrapper returns -1.  My wrapper will return
            // the following:
            // On error, r==-1.
            // On async requests that are still pending, r==-1 w/ errorCode==ERROR_IO_PENDING
            // on async requests that completed sequentially, r==0
            // You will NEVER RELIABLY be able to get the number of bytes
            // read back from this call when using overlapped structures!  You must
            // not pass in a non-null lpNumBytesRead to ReadFile when using
            // overlapped structures!  This is by design NT behavior.
            if (r == -1)
            {
                // For pipes, when they hit EOF, they will come here.
                if (errorCode == ERROR_BROKEN_PIPE)
                {
                    // Not an error, but EOF.  AsyncFSCallback will NOT be
                    // called.  Call the user callback here.

                    // We clear the overlapped status bit for this special case.
                    // Failure to do so looks like we are freeing a pending overlapped later.
                    intOverlapped->InternalLow = IntPtr.Zero;
                    completionSource.SetCompletedSynchronously(0);
                }
                else if (errorCode != ERROR_IO_PENDING)
                {
                    if (!_fileHandle.IsClosed && CanSeek)  // Update Position - It could be anywhere.
                    {
                        SeekCore(_fileHandle, 0, SeekOrigin.Current);
                    }

                    completionSource.ReleaseNativeResource();

                    if (errorCode == ERROR_HANDLE_EOF)
                    {
                        throw Error.GetEndOfFile();
                    }
                    else
                    {
                        throw Win32Marshal.GetExceptionForWin32Error(errorCode, _path);
                    }
                }
                else if (cancellationToken.CanBeCanceled) // ERROR_IO_PENDING
                {
                    // Only once the IO is pending do we register for cancellation
                    completionSource.RegisterForCancellation(cancellationToken);
                }
            }
            else
            {
                // Due to a workaround for a race condition in NT's ReadFile &
                // WriteFile routines, we will always be returning 0 from ReadFileNative
                // when we do async IO instead of the number of bytes read,
                // irregardless of whether the operation completed
                // synchronously or asynchronously.  We absolutely must not
                // set asyncResult._numBytes here, since will never have correct
                // results.
            }

            return completionSource.Task;
        }

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsyncInternal(new ReadOnlyMemory<byte>(buffer, offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsyncInternal(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => WriteAsyncInternal(buffer, cancellationToken);

        // Instance method to help code external to this MarshalByRefObject avoid
        // accessing its fields by ref.  This avoids a compiler warning.
        private FileStreamCompletionSource? CompareExchangeCurrentOverlappedOwner(FileStreamCompletionSource? newSource, FileStreamCompletionSource? existingSource) =>
            Interlocked.CompareExchange(ref _currentOverlappedOwner, newSource, existingSource);

        private ValueTask WriteAsyncInternal(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
            => new ValueTask(WriteAsyncInternalCore(source, cancellationToken));

        private unsafe Task WriteAsyncInternalCore(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
        {
            Debug.Assert(CanWrite, "BufferedStream has already verified that");
            Debug.Assert(!_fileHandle.IsClosed, "!_handle.IsClosed");

            // Create and store async stream class library specific data in the async result
            FileStreamCompletionSource completionSource = FileStreamCompletionSource.Create(this, 0, source);
            NativeOverlapped* intOverlapped = completionSource.Overlapped;

            if (CanSeek)
            {
                // Make sure we set the length of the file appropriately.
                long len = Length;

                // Make sure we are writing to the position that we think we are
                VerifyOSHandlePosition();

                if (_filePosition + source.Length > len)
                {
                    SetLengthCore(_filePosition + source.Length);
                }

                // Now set the position to read from in the NativeOverlapped struct
                // For pipes, we should leave the offset fields set to 0.
                intOverlapped->OffsetLow = (int)_filePosition;
                intOverlapped->OffsetHigh = (int)(_filePosition >> 32);

                // When using overlapped IO, the OS is not supposed to
                // touch the file pointer location at all.  We will adjust it
                // ourselves.  This isn't threadsafe.
                SeekCore(_fileHandle, source.Length, SeekOrigin.Current);
            }

            // queue an async WriteFile operation and pass in a packed overlapped
            int r = WriteFileNative(_fileHandle, source.Span, intOverlapped, out int errorCode);

            // WriteFile, the OS version, will return 0 on failure.  But
            // my WriteFileNative wrapper returns -1.  My wrapper will return
            // the following:
            // On error, r==-1.
            // On async requests that are still pending, r==-1 w/ errorCode==ERROR_IO_PENDING
            // On async requests that completed sequentially, r==0
            // You will NEVER RELIABLY be able to get the number of bytes
            // written back from this call when using overlapped IO!  You must
            // not pass in a non-null lpNumBytesWritten to WriteFile when using
            // overlapped structures!  This is ByDesign NT behavior.
            if (r == -1)
            {
                // For pipes, when they are closed on the other side, they will come here.
                if (errorCode == ERROR_NO_DATA)
                {
                    // Not an error, but EOF. AsyncFSCallback will NOT be called.
                    // Completing TCS and return cached task allowing the GC to collect TCS.
                    completionSource.SetCompletedSynchronously(0);
                    return Task.CompletedTask;
                }
                else if (errorCode != ERROR_IO_PENDING)
                {
                    if (!_fileHandle.IsClosed && CanSeek)  // Update Position - It could be anywhere.
                    {
                        SeekCore(_fileHandle, 0, SeekOrigin.Current);
                    }

                    completionSource.ReleaseNativeResource();

                    if (errorCode == ERROR_HANDLE_EOF)
                    {
                        throw Error.GetEndOfFile();
                    }
                    else
                    {
                        throw Win32Marshal.GetExceptionForWin32Error(errorCode, _path);
                    }
                }
                else if (cancellationToken.CanBeCanceled) // ERROR_IO_PENDING
                {
                    // Only once the IO is pending do we register for cancellation
                    completionSource.RegisterForCancellation(cancellationToken);
                }
            }
            else
            {
                // Due to a workaround for a race condition in NT's ReadFile &
                // WriteFile routines, we will always be returning 0 from WriteFileNative
                // when we do async IO instead of the number of bytes written,
                // irregardless of whether the operation completed
                // synchronously or asynchronously.  We absolutely must not
                // set asyncResult._numBytes here, since will never have correct
                // results.
            }

            return completionSource.Task;
        }

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            ValidateCopyToArguments(destination, bufferSize);

            // Fail if the file was closed
            if (_fileHandle.IsClosed)
            {
                throw Error.GetFileNotOpen();
            }
            if (!CanRead)
            {
                throw Error.GetReadNotSupported();
            }

            // Bail early for cancellation if cancellation has been requested
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<int>(cancellationToken);
            }

            return AsyncModeCopyToAsync(destination, bufferSize, cancellationToken);
        }

        private async Task AsyncModeCopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            Debug.Assert(!_fileHandle.IsClosed, "!_handle.IsClosed");
            Debug.Assert(CanRead, "_parent.CanRead");

            // For efficiency, we avoid creating a new task and associated state for each asynchronous read.
            // Instead, we create a single reusable awaitable object that will be triggered when an await completes
            // and reset before going again.
            var readAwaitable = new AsyncCopyToAwaitable(this);

            // Make sure we are reading from the position that we think we are.
            // Only set the position in the awaitable if we can seek (e.g. not for pipes).
            bool canSeek = CanSeek;
            if (canSeek)
            {
                VerifyOSHandlePosition();
                readAwaitable._position = _filePosition;
            }

            // Get the buffer to use for the copy operation, as the base CopyToAsync does. We don't try to use
            // _buffer here, even if it's not null, as concurrent operations are allowed, and another operation may
            // actually be using the buffer already. Plus, it'll be rare for _buffer to be non-null, as typically
            // CopyToAsync is used as the only operation performed on the stream, and the buffer is lazily initialized.
            // Further, typically the CopyToAsync buffer size will be larger than that used by the FileStream, such that
            // we'd likely be unable to use it anyway.  Instead, we rent the buffer from a pool.
            byte[] copyBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);

            // Allocate an Overlapped we can use repeatedly for all operations
            var awaitableOverlapped = new PreAllocatedOverlapped(AsyncCopyToAwaitable.s_callback, readAwaitable, copyBuffer);
            var cancellationReg = default(CancellationTokenRegistration);
            try
            {
                // Register for cancellation.  We do this once for the whole copy operation, and just try to cancel
                // whatever read operation may currently be in progress, if there is one.  It's possible the cancellation
                // request could come in between operations, in which case we flag that with explicit calls to ThrowIfCancellationRequested
                // in the read/write copy loop.
                if (cancellationToken.CanBeCanceled)
                {
                    cancellationReg = cancellationToken.UnsafeRegister(static s =>
                    {
                        Debug.Assert(s is AsyncCopyToAwaitable);
                        var innerAwaitable = (AsyncCopyToAwaitable)s;
                        unsafe
                        {
                            lock (innerAwaitable.CancellationLock) // synchronize with cleanup of the overlapped
                            {
                                if (innerAwaitable._nativeOverlapped != null)
                                {
                                    // Try to cancel the I/O.  We ignore the return value, as cancellation is opportunistic and we
                                    // don't want to fail the operation because we couldn't cancel it.
                                    Interop.Kernel32.CancelIoEx(innerAwaitable._fileStream._fileHandle, innerAwaitable._nativeOverlapped);
                                }
                            }
                        }
                    }, readAwaitable);
                }

                // Repeatedly read from this FileStream and write the results to the destination stream.
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    readAwaitable.ResetForNextOperation();

                    try
                    {
                        bool synchronousSuccess;
                        int errorCode;
                        unsafe
                        {
                            // Allocate a native overlapped for our reusable overlapped, and set position to read based on the next
                            // desired address stored in the awaitable.  (This position may be 0, if either we're at the beginning or
                            // if the stream isn't seekable.)
                            readAwaitable._nativeOverlapped = _fileHandle.ThreadPoolBinding!.AllocateNativeOverlapped(awaitableOverlapped);
                            if (canSeek)
                            {
                                readAwaitable._nativeOverlapped->OffsetLow = unchecked((int)readAwaitable._position);
                                readAwaitable._nativeOverlapped->OffsetHigh = (int)(readAwaitable._position >> 32);
                            }

                            // Kick off the read.
                            synchronousSuccess = ReadFileNative(_fileHandle, copyBuffer, readAwaitable._nativeOverlapped, out errorCode) >= 0;
                        }

                        // If the operation did not synchronously succeed, it either failed or initiated the asynchronous operation.
                        if (!synchronousSuccess)
                        {
                            switch (errorCode)
                            {
                                case ERROR_IO_PENDING:
                                    // Async operation in progress.
                                    break;
                                case ERROR_BROKEN_PIPE:
                                case ERROR_HANDLE_EOF:
                                    // We're at or past the end of the file, and the overlapped callback
                                    // won't be raised in these cases. Mark it as completed so that the await
                                    // below will see it as such.
                                    readAwaitable.MarkCompleted();
                                    break;
                                default:
                                    // Everything else is an error (and there won't be a callback).
                                    throw Win32Marshal.GetExceptionForWin32Error(errorCode, _path);
                            }
                        }

                        // Wait for the async operation (which may or may not have already completed), then throw if it failed.
                        await readAwaitable;
                        switch (readAwaitable._errorCode)
                        {
                            case 0: // success
                                break;
                            case ERROR_BROKEN_PIPE: // logically success with 0 bytes read (write end of pipe closed)
                            case ERROR_HANDLE_EOF:  // logically success with 0 bytes read (read at end of file)
                                Debug.Assert(readAwaitable._numBytes == 0, $"Expected 0 bytes read, got {readAwaitable._numBytes}");
                                break;
                            case Interop.Errors.ERROR_OPERATION_ABORTED: // canceled
                                throw new OperationCanceledException(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(true));
                            default: // error
                                throw Win32Marshal.GetExceptionForWin32Error((int)readAwaitable._errorCode, _path);
                        }

                        // Successful operation.  If we got zero bytes, we're done: exit the read/write loop.
                        int numBytesRead = (int)readAwaitable._numBytes;
                        if (numBytesRead == 0)
                        {
                            break;
                        }

                        // Otherwise, update the read position for next time accordingly.
                        if (canSeek)
                        {
                            readAwaitable._position += numBytesRead;
                        }
                    }
                    finally
                    {
                        // Free the resources for this read operation
                        unsafe
                        {
                            NativeOverlapped* overlapped;
                            lock (readAwaitable.CancellationLock) // just an Exchange, but we need this to be synchronized with cancellation, so using the same lock
                            {
                                overlapped = readAwaitable._nativeOverlapped;
                                readAwaitable._nativeOverlapped = null;
                            }
                            if (overlapped != null)
                            {
                                _fileHandle.ThreadPoolBinding!.FreeNativeOverlapped(overlapped);
                            }
                        }
                    }

                    // Write out the read data.
                    await destination.WriteAsync(new ReadOnlyMemory<byte>(copyBuffer, 0, (int)readAwaitable._numBytes), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                // Cleanup from the whole copy operation
                cancellationReg.Dispose();
                awaitableOverlapped.Dispose();

                ArrayPool<byte>.Shared.Return(copyBuffer);

                // Make sure the stream's current position reflects where we ended up
                if (!_fileHandle.IsClosed && CanSeek)
                {
                    SeekCore(_fileHandle, 0, SeekOrigin.End);
                }
            }
        }

        /// <summary>Used by CopyToAsync to enable awaiting the result of an overlapped I/O operation with minimal overhead.</summary>
        private sealed unsafe class AsyncCopyToAwaitable : ICriticalNotifyCompletion
        {
            /// <summary>Sentinel object used to indicate that the I/O operation has completed before being awaited.</summary>
            private static readonly Action s_sentinel = () => { };
            /// <summary>Cached delegate to IOCallback.</summary>
            internal static readonly IOCompletionCallback s_callback = IOCallback;

            /// <summary>The FileStream that owns this instance.</summary>
            internal readonly AsyncWindowsFileStreamStrategy _fileStream;

            /// <summary>Tracked position representing the next location from which to read.</summary>
            internal long _position;
            /// <summary>The current native overlapped pointer.  This changes for each operation.</summary>
            internal NativeOverlapped* _nativeOverlapped;
            /// <summary>
            /// null if the operation is still in progress,
            /// s_sentinel if the I/O operation completed before the await,
            /// s_callback if it completed after the await yielded.
            /// </summary>
            internal Action? _continuation;
            /// <summary>Last error code from completed operation.</summary>
            internal uint _errorCode;
            /// <summary>Last number of read bytes from completed operation.</summary>
            internal uint _numBytes;

            /// <summary>Lock object used to protect cancellation-related access to _nativeOverlapped.</summary>
            internal object CancellationLock => this;

            /// <summary>Initialize the awaitable.</summary>
            internal AsyncCopyToAwaitable(AsyncWindowsFileStreamStrategy fileStream)
            {
                _fileStream = fileStream;
            }

            /// <summary>Reset state to prepare for the next read operation.</summary>
            internal void ResetForNextOperation()
            {
                Debug.Assert(_position >= 0, $"Expected non-negative position, got {_position}");
                _continuation = null;
                _errorCode = 0;
                _numBytes = 0;
            }

            /// <summary>Overlapped callback: store the results, then invoke the continuation delegate.</summary>
            internal static void IOCallback(uint errorCode, uint numBytes, NativeOverlapped* pOVERLAP)
            {
                var awaitable = (AsyncCopyToAwaitable?)ThreadPoolBoundHandle.GetNativeOverlappedState(pOVERLAP);
                Debug.Assert(awaitable != null);

                Debug.Assert(!ReferenceEquals(awaitable._continuation, s_sentinel), "Sentinel must not have already been set as the continuation");
                awaitable._errorCode = errorCode;
                awaitable._numBytes = numBytes;

                (awaitable._continuation ?? Interlocked.CompareExchange(ref awaitable._continuation, s_sentinel, null))?.Invoke();
            }

            /// <summary>
            /// Called when it's known that the I/O callback for an operation will not be invoked but we'll
            /// still be awaiting the awaitable.
            /// </summary>
            internal void MarkCompleted()
            {
                Debug.Assert(_continuation == null, "Expected null continuation");
                _continuation = s_sentinel;
            }

            public AsyncCopyToAwaitable GetAwaiter() => this;
            public bool IsCompleted => ReferenceEquals(_continuation, s_sentinel);
            public void GetResult() { }
            public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);
            public void UnsafeOnCompleted(Action continuation)
            {
                if (ReferenceEquals(_continuation, s_sentinel) ||
                    Interlocked.CompareExchange(ref _continuation, continuation, null) != null)
                {
                    Debug.Assert(ReferenceEquals(_continuation, s_sentinel), $"Expected continuation set to s_sentinel, got ${_continuation}");
                    Task.Run(continuation);
                }
            }
        }
    }
}
