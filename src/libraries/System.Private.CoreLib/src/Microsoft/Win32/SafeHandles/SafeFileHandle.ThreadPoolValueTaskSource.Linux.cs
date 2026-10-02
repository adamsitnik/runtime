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

            // Pins and the handle reference span the logical operation, including partial-write
            // continuations, and are released before this instance can be reused.
            private bool _fileHandleRefAdded;
            private MemoryHandle _singleSegmentPin;
            private int _singleSegmentOffset;
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

                    Debug.Assert(!_fileHandleRefAdded);
                    _fileHandle.DangerousAddRef(ref _fileHandleRefAdded);
                    if (_operation is Operation.Read or Operation.Write)
                    {
                        _singleSegmentPin = _singleSegment.Pin();
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
            private bool TryContinueFromIoUring(int result)
            {
                if (result >= 0 && TryContinuePartialWrite(ref result))
                {
                    return true;
                }

                _ioUringResult = result;
                _completedViaIoUring = true;
                return false;
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
                        if (_owner.TryContinueFromIoUring(result))
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
            /// Returns true only when a partial write's continuation was accepted. Otherwise,
            /// updates the terminal result for cancellation or zero progress as necessary.
            /// </summary>
            private bool TryContinuePartialWrite(ref int result)
            {
                if (_operation == Operation.Write)
                {
                    if (result >= _singleSegment.Length)
                    {
                        return false;
                    }

                    // Keep the remaining length for FileStream's incomplete-operation position fixup.
                    // Slicing does not invalidate the original pin.
                    _singleSegment = _singleSegment.Slice(result);
                    _singleSegmentOffset += result;
                }
                else if (_operation == Operation.WriteGather)
                {
                    _remainingBytesToWrite -= result;
                    if (_remainingBytesToWrite <= 0)
                    {
                        return false;
                    }

                    _vectorsOffset += Interop.Sys.AdvanceIOVectors(_vectors.AsSpan(_vectorsOffset), result);
                }
                else
                {
                    return false;
                }

                _fileOffset += result;
                bool canceled = _ioUringOperation!.CancellationIsRequested;
                if (canceled || result == 0)
                {
                    result = -Interop.Sys.ConvertErrorPalToPlatform(canceled ? Interop.Error.ECANCELED : Interop.Error.EIO);
                    return false;
                }

                if (_operation == Operation.Write)
                {
                    SubmitSingleSegment();
                }
                else
                {
                    SubmitVectorRequest();
                }

                return true;
            }

            private unsafe void SubmitSingleSegment()
            {
                Debug.Assert(_operation is Operation.Read or Operation.Write);
                Debug.Assert(_fileHandleRefAdded);

                Interop.Sys.IoRingRequest request = default;
                request.OpCode = _operation == Operation.Read ? Interop.Sys.IoRingOp.Read : Interop.Sys.IoRingOp.Write;
                request.Offset = _fileHandle.SupportsRandomAccess ? _fileOffset : -1;
                request.Buffer = (byte*)_singleSegmentPin.Pointer + _singleSegmentOffset;
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
            /// and array with a window advanced by <see cref="Interop.Sys.AdvanceIOVectors"/>.
            /// </summary>
            private unsafe void SubmitVectorRequest()
            {
                Debug.Assert(_vectors != null && _vectorPins != null && _vectorsHandle.IsAllocated);
                int remainingCount = _vectors.Length - _vectorsOffset;
                Debug.Assert(remainingCount > 0 && _fileHandleRefAdded);

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
                _singleSegmentOffset = 0;

                if (_fileHandleRefAdded)
                {
                    _fileHandle.DangerousRelease();
                    _fileHandleRefAdded = false;
                }
            }
        }
    }
}
