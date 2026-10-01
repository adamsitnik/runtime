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
                if (!PortableThreadPool.IoUringThreadPool.IsEnabled)
                {
                    return;
                }

                try
                {
                    // Empty lists keep the existing worker path, before any pinning or binding.
                    if ((_operation == Operation.ReadScatter && _readScatterBuffers!.Count == 0) ||
                        (_operation == Operation.WriteGather && _writeGatherBuffers!.Count == 0))
                    {
                        return;
                    }

                    if (_operation is Operation.Read or Operation.Write)
                    {
                        SubmitSingleSegment();
                    }
                    else
                    {
                        Debug.Assert(_operation is Operation.ReadScatter or Operation.WriteGather);
                        SubmitVectors();
                    }
                    queued = true;
                }
                catch (Exception error)
                {
                    // Fault the returned ValueTask through ExecuteInternal, including FileStream
                    // position correction. Do not retry a failed submission as blocking I/O.
                    _ioUringSubmissionError = error;
                }
            }

            partial void TryCompleteAsync(ref bool completed, ref long result, ref Exception? exception)
            {
                if (_ioUringSubmissionError is null && !_completedViaIoUring)
                {
                    return;
                }

                completed = true;
                try
                {
                    // A failed enqueue never published native work; otherwise its terminal CQE
                    // has already retired. Both paths release partially or fully acquired state here.
                    ReleaseIoUringState();
                    if (_ioUringSubmissionError is not null)
                    {
                        exception = _ioUringSubmissionError;
                    }
                    else if (_ioUringResult < 0)
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
            /// this instance when cancellation or a zero-progress write must be delivered instead.
            /// Submission exceptions propagate to the completion handler.
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

                    // The old pin is no longer valid once we reslice; SubmitSingleSegment re-pins the
                    // remainder. _context was already captured when the operation was originally queued.
                    _singleSegmentPin.Dispose();
                    _singleSegmentPin = default;
                    if (_fileHandleRefAdded)
                    {
                        _fileHandle.DangerousRelease();
                        _fileHandleRefAdded = false;
                    }

                    SubmitSingleSegment();

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

                    SubmitVectorRequest();

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

            private unsafe void SubmitSingleSegment()
            {
                Debug.Assert(_operation is Operation.Read or Operation.Write);
                Debug.Assert(!_fileHandleRefAdded);
                _fileHandle.DangerousAddRef(ref _fileHandleRefAdded);
                _singleSegmentPin = _singleSegment.Pin();

                Interop.Sys.IoRingRequest request = default;
                request.OpCode = _operation == Operation.Read ? Interop.Sys.IoRingOp.Read : Interop.Sys.IoRingOp.Write;
                request.Offset = _fileHandle.SupportsRandomAccess ? _fileOffset : -1;
                request.Buffer = (byte*)_singleSegmentPin.Pointer;
                request.BufferLength = _singleSegment.Length;

                // Publish all ownership state before the request can complete on another worker.
                EnqueueIoUring(in request);
            }

            private unsafe void SubmitVectors()
            {
                bool write = _operation == Operation.WriteGather;
                int count = write ? _writeGatherBuffers!.Count : _readScatterBuffers!.Count;
                Debug.Assert(count > 0);

                // Store ownership incrementally so the common completion cleanup also handles
                // a failed allocation or a MemoryManager throwing partway through pinning.
                _vectorPins = new MemoryHandle[count];
                _vectors = new Interop.Sys.IOVector[count];
                _vectorsOffset = 0;
                long totalBytes = 0;
                for (int i = 0; i < count; i++)
                {
                    ReadOnlyMemory<byte> buffer = write ? _writeGatherBuffers![i] : _readScatterBuffers![i];
                    _vectorPins[i] = buffer.Pin();
                    _vectors[i] = new Interop.Sys.IOVector { Base = (byte*)_vectorPins[i].Pointer, Count = (UIntPtr)buffer.Length };
                    totalBytes += buffer.Length;
                }

                _remainingBytesToWrite = totalBytes;
                _vectorsHandle = GCHandle.Alloc(_vectors, GCHandleType.Pinned);
                SubmitVectorRequest();
            }

            /// <summary>
            /// Enqueues the pinned vectors for a read or write. Partial writes reuse the same pins
            /// and array with a window advanced by <see cref="AdvanceVectorsAfterPartialWrite"/>.
            /// </summary>
            private unsafe void SubmitVectorRequest()
            {
                Debug.Assert(_vectors != null && _vectorPins != null && _vectorsHandle.IsAllocated);
                int remainingCount = _vectors.Length - _vectorsOffset;
                Debug.Assert(remainingCount > 0 && !_fileHandleRefAdded);
                _fileHandle.DangerousAddRef(ref _fileHandleRefAdded);

                Interop.Sys.IoRingRequest request = default;
                request.OpCode = _operation == Operation.ReadScatter ? Interop.Sys.IoRingOp.ReadV : Interop.Sys.IoRingOp.WriteV;
                request.Offset = _fileHandle.SupportsRandomAccess ? _fileOffset : -1;
                request.Vectors = (Interop.Sys.IOVector*)_vectorsHandle.AddrOfPinnedObject() + _vectorsOffset;
                request.VectorCount = remainingCount;
                EnqueueIoUring(in request);
            }

            /// <summary>
            /// Releases pinning/ref-counting state after an unpublished failure or terminal native
            /// completion. Partially acquired state is valid; kernel-live state must never be released.
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
                _vectorsOffset = 0;
                _remainingBytesToWrite = 0;

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
