// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Microsoft.Win32.SafeHandles
{
    public sealed partial class SafeFileHandle
    {
        internal sealed partial class ThreadPoolValueTaskSource
        {
            // io_uring completion state. When _completedViaIoUring is true, TryCompleteAsync finalizes
            // the operation using _ioUringResult instead of performing a blocking syscall.
            private bool _completedViaIoUring;
            private int _ioUringResult;
            private Exception? _ioUringSubmissionError;
            private FileIoUringOperation? _ioUringOperation;

            // io_uring in-flight pinning/ref-counting state. These are populated only while an io_uring
            // submission for this instance is outstanding, and are always fully cleaned up (pins
            // disposed, SafeHandle ref released) before the operation is considered complete/reusable.
            private bool _fileHandleRefAdded;
            private MemoryHandle _singleSegmentPin;
            private MemoryHandle[]? _vectorPins;
            private Interop.Sys.IOVector[]? _vectors;
            private GCHandle _vectorsHandle;
            // For WriteGather partial-write retries: index of the first not-yet-fully-written vector,
            // and the number of bytes still left to write across the remaining vectors.
            private int _vectorsOffset;
            private long _remainingBytesToWrite;

            partial void TryQueueAsync(ref bool queued)
            {
                queued = _operation switch
                {
                    Operation.Read => TrySubmitRead(),
                    Operation.Write => TrySubmitWrite(),
                    Operation.ReadScatter => TrySubmitReadScatter(),
                    Operation.WriteGather => TrySubmitWriteGather(),
                    _ => false
                };
            }

            partial void TryCompleteAsync(ref bool completed, ref long result, ref Exception? exception)
            {
                try
                {
                    if (_ioUringSubmissionError is not null)
                    {
                        completed = true;
                        ReleaseIoUringState();
                        exception = _ioUringSubmissionError;
                    }
                    else if (_completedViaIoUring)
                    {
                        completed = true;
                        // The kernel already read from / wrote to the pinned buffer(s) directly; release
                        // the pins/ref now that the operation has fully completed (successfully or not).
                        ReleaseIoUringState();

                        if (_ioUringResult < 0)
                        {
                            exception = new Interop.ErrorInfo(-_ioUringResult).Error == Interop.Error.ECANCELED &&
                                _cancellationToken.IsCancellationRequested
                                ? new OperationCanceledException(_cancellationToken)
                                : Interop.GetExceptionForIoErrno(new Interop.ErrorInfo(-_ioUringResult), _fileHandle.Path);
                        }
                        else
                        {
                            result = _ioUringResult;
                        }
                    }
                }
                finally
                {
                    _completedViaIoUring = false;
                    _ioUringResult = 0;
                    _ioUringSubmissionError = null;
                }
            }

            /// <summary>
            /// Performs completion bookkeeping on a worker and continues partial writes.
            /// </summary>
            private IThreadPoolWorkItem? CompleteFromIoUring(int result)
            {
                if (result >= 0 && (_operation == Operation.Write || _operation == Operation.WriteGather)
                    && TryContinuePartialWrite(result, out IThreadPoolWorkItem? completionWorkItem))
                {
                    return completionWorkItem;
                }

                _ioUringResult = result;
                _completedViaIoUring = true;
                return this;
            }

            private void EnqueueIoUring(in Interop.Sys.IoRingRequest request)
            {
                FileIoUringOperation operation = _ioUringOperation ??=
                    new FileIoUringOperation(this, _fileHandle.IoUringBinding);
                operation.Enqueue(in request);
            }

            private sealed class FileIoUringOperation : IoUringOperation
            {
                private readonly ThreadPoolValueTaskSource _owner;
                private readonly IoRingBoundHandle _boundHandle;
                private IoUringRequest _request;
                private bool _active;

                public FileIoUringOperation(ThreadPoolValueTaskSource owner, IoRingBoundHandle binding)
                {
                    _owner = owner;
                    _boundHandle = binding;
                }

                protected override IoUringRequest Request => _request;

                public void Enqueue(in Interop.Sys.IoRingRequest request)
                {
                    _request = new IoUringRequest(in request);
                    if (_active)
                    {
                        EnqueueContinuation(_request);
                    }
                    else
                    {
                        _active = true;
                        try
                        {
                            _boundHandle.EnqueueForSubmission(this, _owner._cancellationToken);
                        }
                        catch
                        {
                            _active = false;
                            throw;
                        }
                    }
                }

                protected override void OnCompleted(int result, uint flags, long sequence)
                {
                    try
                    {
                        if (_owner.CompleteFromIoUring(result) is null)
                        {
                            return;
                        }
                    }
                    catch (Exception error)
                    {
                        _owner._ioUringSubmissionError = error;
                    }
                    CompleteOperation();
                    _active = false;
                    _request = default;
                    ((IThreadPoolWorkItem)_owner).Execute();
                }
            }

            /// <summary>
            /// If <paramref name="bytesWritten"/> represents a partial write (fewer bytes than were
            /// requested by the most recent submission), advances the write state and attempts to
            /// resubmit an io_uring request for the remainder. Returns true if the result was partial.
            /// <paramref name="completionWorkItem"/> is null when a continuation was accepted, or
            /// this instance when cancellation or a submission error must be delivered instead.
            /// </summary>
            private bool TryContinuePartialWrite(int bytesWritten, out IThreadPoolWorkItem? completionWorkItem)
            {
                completionWorkItem = null;

                if (_operation == Operation.Write)
                {
                    if (bytesWritten >= _singleSegment.Length)
                    {
                        return false;
                    }

                    _singleSegment = _singleSegment.Slice(bytesWritten);
                    _fileOffset += bytesWritten;
                    if (StopPartialWrite(bytesWritten))
                    {
                        completionWorkItem = this;
                        return true;
                    }

                    // The old pin is no longer valid once we reslice; TrySubmitWrite re-pins the
                    // remainder. _context was already captured when the operation was originally queued.
                    _singleSegmentPin.Dispose();
                    _singleSegmentPin = default;
                    if (_fileHandleRefAdded)
                    {
                        _fileHandle.DangerousRelease();
                        _fileHandleRefAdded = false;
                    }

                    if (!TrySubmitWrite())
                    {
                        completionWorkItem = this;
                    }

                    return true;
                }

                if (_operation == Operation.WriteGather)
                {
                    _remainingBytesToWrite -= bytesWritten;
                    if (_remainingBytesToWrite <= 0)
                    {
                        return false;
                    }

                    _fileOffset += bytesWritten;
                    AdvanceVectorsAfterPartialWrite(bytesWritten);
                    if (StopPartialWrite(bytesWritten))
                    {
                        completionWorkItem = this;
                        return true;
                    }

                    // Release just the file-handle ref added for the previous submission; the vector
                    // pins/array remain valid and are reused (with an adjusted window) for the resubmit.
                    if (_fileHandleRefAdded)
                    {
                        _fileHandle.DangerousRelease();
                        _fileHandleRefAdded = false;
                    }

                    if (!TrySubmitWriteGatherRemainder())
                    {
                        completionWorkItem = this;
                    }

                    return true;
                }

                return false;
            }

            private bool StopPartialWrite(int bytesWritten)
            {
                bool canceled = _ioUringOperation!.CancellationIsRequested;
                if (!canceled && bytesWritten != 0)
                {
                    return false;
                }
                _ioUringResult = -Interop.Sys.ConvertErrorPalToPlatform(canceled ? Interop.Error.ECANCELED : Interop.Error.EIO);
                _completedViaIoUring = true;
                return true;
            }

            /// <summary>
            /// Mirrors the bookkeeping in the blocking <see cref="RandomAccess.WriteGatherAtOffset"/>
            /// implementation: advances <see cref="_vectorsOffset"/> past any vectors that were fully
            /// written, and adjusts the base/length of the first partially-written vector in place.
            /// </summary>
            private void AdvanceVectorsAfterPartialWrite(int bytesWritten)
            {
                Debug.Assert(_vectors != null);
                Interop.Sys.IOVector[] vectors = _vectors;
                int count = vectors.Length;

                while (_vectorsOffset < count && bytesWritten > 0)
                {
                    int n = (int)vectors[_vectorsOffset].Count;
                    if (n <= bytesWritten)
                    {
                        bytesWritten -= n;
                        _vectorsOffset++;
                    }
                    else
                    {
                        unsafe
                        {
                            Interop.Sys.IOVector current = vectors[_vectorsOffset];
                            vectors[_vectorsOffset] = new Interop.Sys.IOVector
                            {
                                Base = current.Base + bytesWritten,
                                Count = current.Count - (UIntPtr)bytesWritten
                            };
                        }
                        break;
                    }
                }
            }

            private unsafe bool TrySubmitRead()
            {
                if (!PortableThreadPool.IoUringThreadPool.IsEnabled)
                {
                    return false;
                }

                bool refAdded = false;
                try
                {
                    _fileHandle.DangerousAddRef(ref refAdded);
                    _singleSegmentPin = _singleSegment.Pin();

                    Interop.Sys.IoRingRequest request = default;
                    request.OpCode = Interop.Sys.IoRingOp.Read;
                    request.Fd = _fileHandle.DangerousGetHandle();
                    request.Offset = _fileHandle.SupportsRandomAccess ? _fileOffset : -1;
                    request.Buffer = (byte*)_singleSegmentPin.Pointer;
                    request.BufferLength = _singleSegment.Length;

                    // Completion may run as soon as the request is published.
                    _fileHandleRefAdded = refAdded;
                    EnqueueIoUring(in request);
                    return true;
                }
                catch (Exception error)
                {
                    _ioUringSubmissionError = error;
                }

                _singleSegmentPin.Dispose();
                _singleSegmentPin = default;
                _fileHandleRefAdded = false;
                if (refAdded)
                {
                    _fileHandle.DangerousRelease();
                }
                return false;
            }

            private unsafe bool TrySubmitWrite()
            {
                if (!PortableThreadPool.IoUringThreadPool.IsEnabled)
                {
                    return false;
                }

                bool refAdded = false;
                try
                {
                    _fileHandle.DangerousAddRef(ref refAdded);
                    _singleSegmentPin = _singleSegment.Pin();

                    Interop.Sys.IoRingRequest request = default;
                    request.OpCode = Interop.Sys.IoRingOp.Write;
                    request.Fd = _fileHandle.DangerousGetHandle();
                    request.Offset = _fileHandle.SupportsRandomAccess ? _fileOffset : -1;
                    request.Buffer = (byte*)_singleSegmentPin.Pointer;
                    request.BufferLength = _singleSegment.Length;

                    _fileHandleRefAdded = refAdded;
                    EnqueueIoUring(in request);
                    return true;
                }
                catch (Exception error)
                {
                    _ioUringSubmissionError = error;
                }

                _singleSegmentPin.Dispose();
                _singleSegmentPin = default;
                _fileHandleRefAdded = false;
                if (refAdded)
                {
                    _fileHandle.DangerousRelease();
                }
                return false;
            }

            private unsafe bool TrySubmitReadScatter()
            {
                if (!PortableThreadPool.IoUringThreadPool.IsEnabled)
                {
                    return false;
                }

                Debug.Assert(_readScatterBuffers != null);
                int count = _readScatterBuffers.Count;
                if (count == 0)
                {
                    return false;
                }

                bool refAdded = false;
                MemoryHandle[] pins = new MemoryHandle[count];
                Interop.Sys.IOVector[] vectors = new Interop.Sys.IOVector[count];
                GCHandle vectorsHandle = default;
                int pinned = 0;
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        Memory<byte> buffer = _readScatterBuffers[i];
                        MemoryHandle pin = buffer.Pin();
                        pins[i] = pin;
                        pinned = i + 1;
                        vectors[i] = new Interop.Sys.IOVector { Base = (byte*)pin.Pointer, Count = (UIntPtr)buffer.Length };
                    }

                    vectorsHandle = GCHandle.Alloc(vectors, GCHandleType.Pinned);
                    _fileHandle.DangerousAddRef(ref refAdded);

                    Interop.Sys.IoRingRequest request = default;
                    request.OpCode = Interop.Sys.IoRingOp.ReadV;
                    request.Fd = _fileHandle.DangerousGetHandle();
                    request.Offset = _fileHandle.SupportsRandomAccess ? _fileOffset : -1;
                    request.Vectors = (Interop.Sys.IOVector*)vectorsHandle.AddrOfPinnedObject();
                    request.VectorCount = count;

                    _vectorPins = pins;
                    _vectors = vectors;
                    _vectorsHandle = vectorsHandle;
                    _fileHandleRefAdded = refAdded;
                    EnqueueIoUring(in request);
                    return true;
                }
                catch (Exception error)
                {
                    _ioUringSubmissionError = error;
                }

                _vectorPins = null;
                _vectors = null;
                _vectorsHandle = default;
                _fileHandleRefAdded = false;
                if (vectorsHandle.IsAllocated)
                {
                    vectorsHandle.Free();
                }
                for (int i = 0; i < pinned; i++)
                {
                    pins[i].Dispose();
                }
                if (refAdded)
                {
                    _fileHandle.DangerousRelease();
                }
                return false;
            }

            private unsafe bool TrySubmitWriteGather()
            {
                if (!PortableThreadPool.IoUringThreadPool.IsEnabled)
                {
                    return false;
                }

                Debug.Assert(_writeGatherBuffers != null);
                int count = _writeGatherBuffers.Count;
                if (count == 0)
                {
                    return false;
                }

                bool refAdded = false;
                MemoryHandle[] pins = new MemoryHandle[count];
                Interop.Sys.IOVector[] vectors = new Interop.Sys.IOVector[count];
                GCHandle vectorsHandle = default;
                int pinned = 0;
                try
                {
                    long totalBytesToWrite = 0;
                    for (int i = 0; i < count; i++)
                    {
                        ReadOnlyMemory<byte> buffer = _writeGatherBuffers[i];
                        totalBytesToWrite += buffer.Length;

                        MemoryHandle pin = buffer.Pin();
                        pins[i] = pin;
                        pinned = i + 1;
                        vectors[i] = new Interop.Sys.IOVector { Base = (byte*)pin.Pointer, Count = (UIntPtr)buffer.Length };
                    }

                    vectorsHandle = GCHandle.Alloc(vectors, GCHandleType.Pinned);
                    _fileHandle.DangerousAddRef(ref refAdded);

                    Interop.Sys.IoRingRequest request = default;
                    request.OpCode = Interop.Sys.IoRingOp.WriteV;
                    request.Fd = _fileHandle.DangerousGetHandle();
                    request.Offset = _fileHandle.SupportsRandomAccess ? _fileOffset : -1;
                    request.Vectors = (Interop.Sys.IOVector*)vectorsHandle.AddrOfPinnedObject();
                    request.VectorCount = count;

                    _vectorPins = pins;
                    _vectors = vectors;
                    _vectorsHandle = vectorsHandle;
                    _vectorsOffset = 0;
                    _remainingBytesToWrite = totalBytesToWrite;
                    _fileHandleRefAdded = refAdded;
                    EnqueueIoUring(in request);
                    return true;
                }
                catch (Exception error)
                {
                    _ioUringSubmissionError = error;
                }

                _vectorPins = null;
                _vectors = null;
                _vectorsHandle = default;
                _vectorsOffset = 0;
                _remainingBytesToWrite = 0;
                _fileHandleRefAdded = false;
                if (vectorsHandle.IsAllocated)
                {
                    vectorsHandle.Free();
                }
                for (int i = 0; i < pinned; i++)
                {
                    pins[i].Dispose();
                }
                if (refAdded)
                {
                    _fileHandle.DangerousRelease();
                }
                return false;
            }

            /// <summary>
            /// Resubmits the remaining (not-yet-written) portion of a WriteGather operation. Reuses the
            /// already-pinned <see cref="_vectorsHandle"/>/<see cref="_vectorPins"/> from the original
            /// submission (never freed/re-pinned between partial-write retries - only the request's
            /// window into the same pinned array changes), advanced by
            /// <see cref="AdvanceVectorsAfterPartialWrite"/>.
            /// </summary>
            private unsafe bool TrySubmitWriteGatherRemainder()
            {
                if (!PortableThreadPool.IoUringThreadPool.IsEnabled)
                {
                    return false;
                }

                Debug.Assert(_vectors != null && _vectorPins != null && _vectorsHandle.IsAllocated);
                int remainingCount = _vectors.Length - _vectorsOffset;

                bool refAdded = false;
                try
                {
                    _fileHandle.DangerousAddRef(ref refAdded);

                    Interop.Sys.IoRingRequest request = default;
                    request.OpCode = Interop.Sys.IoRingOp.WriteV;
                    request.Fd = _fileHandle.DangerousGetHandle();
                    request.Offset = _fileHandle.SupportsRandomAccess ? _fileOffset : -1;
                    request.Vectors = (Interop.Sys.IOVector*)_vectorsHandle.AddrOfPinnedObject() + _vectorsOffset;
                    request.VectorCount = remainingCount;

                    _fileHandleRefAdded = refAdded;
                    EnqueueIoUring(in request);
                    return true;
                }
                catch (Exception error)
                {
                    _ioUringSubmissionError = error;
                }

                _fileHandleRefAdded = false;
                if (refAdded)
                {
                    _fileHandle.DangerousRelease();
                }
                return false;
            }

            /// <summary>
            /// Releases all pinning/ref-counting state associated with an outstanding (or just-completed)
            /// io_uring submission. Safe to call even if no io_uring submission is currently outstanding.
            /// </summary>
            private void ReleaseIoUringState()
            {
                if (_vectorsHandle.IsAllocated)
                {
                    _vectorsHandle.Free();
                }

                if (_vectorPins != null)
                {
                    foreach (MemoryHandle pin in _vectorPins)
                    {
                        pin.Dispose();
                    }
                    _vectorPins = null;
                }
                _vectors = null;

                _singleSegmentPin.Dispose();
                _singleSegmentPin = default;

                if (_fileHandleRefAdded)
                {
                    _fileHandle.DangerousRelease();
                    _fileHandleRefAdded = false;
                }
            }
        }
    }
}
