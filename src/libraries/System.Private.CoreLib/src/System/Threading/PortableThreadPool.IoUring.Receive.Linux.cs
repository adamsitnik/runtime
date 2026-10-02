// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace System.Threading
{
    internal sealed partial class PortableThreadPool
    {
        internal static partial class IoUringThreadPool
        {
            /// <summary>
            /// Ring-owned pool of provided buffers (buf_group 0) used by every
            /// <see cref="Interop.Sys.IoRingOp.RecvMultishot"/> request submitted to this ring - see
            /// <see cref="Interop.Sys.IoRingRegisterBufferRing"/>. Backed by natively-allocated,
            /// page-aligned storage that the ring itself owns and frees on close. A completion selects a
            /// buffer by id (0..<see cref="BufferCount"/>-1); <see cref="Rent"/> wraps that buffer's
            /// slice as a distinct <see cref="IMemoryOwner{Byte}"/> without copying. The owner must not
            /// be reused: a repeated Dispose on an old owner must never return a subsequent lease.
            /// </summary>
            internal sealed unsafe class ReceiveBufferPool
            {
                private const int BitsPerReturnWord = 64;

                public readonly int BufferSize;
                public readonly int BufferCount;

                private readonly Ring _ring;
                private readonly byte* _storage;
                private readonly long[] _returnedBuffers;
                private MultishotReceiveOperation? _waitingReceives;
                private MultishotReceiveOperation? _lastWaitingReceive;

                // Issuer-owned count: selected buffers leave at CQE reaping, and returns
                // reenter only after being published to the kernel.
                internal int AvailableBuffers;

                internal ReceiveBufferPool(Ring ring, int bufferSize, int bufferCount, byte* storage)
                {
                    _ring = ring;
                    BufferSize = bufferSize;
                    BufferCount = bufferCount;
                    AvailableBuffers = bufferCount;
                    _storage = storage;
                    _returnedBuffers = new long[checked((bufferCount + BitsPerReturnWord - 1) / BitsPerReturnWord)];
                }

                public IMemoryOwner<byte> Rent(int bufferId, int length)
                {
                    Debug.Assert((uint)bufferId < (uint)BufferCount && (uint)length <= (uint)BufferSize);
                    return new ReceiveBufferLease(this, bufferId, _storage + (long)bufferId * BufferSize, length);
                }

                /// <summary>
                /// Queues <paramref name="bufferId"/> to be republished to the kernel - see
                /// <see cref="DrainReturns"/>. Each id has at most one outstanding return, so a
                /// bit per buffer tracks returns without preserving their order.
                /// May be called from any thread (whichever one disposes the corresponding
                /// <see cref="ReceiveBufferLease"/>).
                /// </summary>
                internal void Return(int bufferId)
                {
                    Interlocked.Or(ref _returnedBuffers[bufferId / BitsPerReturnWord], 1L << (bufferId % BitsPerReturnWord));
                    if (Volatile.Read(ref _waitingReceives) is not null)
                    {
                        WakeIssuer(_ring);
                    }
                }

                internal bool HasReadyWaiters => _waitingReceives is not null && AvailableBuffers > 0;

                internal bool HasPendingReturns
                {
                    get
                    {
                        if (_waitingReceives is not null)
                        {
                            foreach (ref long word in _returnedBuffers.AsSpan())
                            {
                                if (Volatile.Read(ref word) != 0)
                                {
                                    return true;
                                }
                            }
                        }
                        return false;
                    }
                }

                internal void AddWaiter(MultishotReceiveOperation operation, in Interop.Sys.IoRingRequest request)
                {
                    Debug.Assert(AvailableBuffers == 0);
                    operation._waitingRequest = request;
                    operation._previousWaiter = _lastWaitingReceive;
                    if (_lastWaitingReceive is null)
                    {
                        Volatile.Write(ref _waitingReceives, operation);
                    }
                    else
                    {
                        _lastWaitingReceive._nextWaiter = operation;
                    }
                    _lastWaitingReceive = operation;
                    LinkOperation(operation, published: false);
                }

                internal void RemoveWaiter(MultishotReceiveOperation operation)
                {
                    if (operation._previousWaiter is null)
                    {
                        Debug.Assert(_waitingReceives == operation);
                        Volatile.Write(ref _waitingReceives, operation._nextWaiter);
                    }
                    else
                    {
                        operation._previousWaiter._nextWaiter = operation._nextWaiter;
                    }
                    if (operation._nextWaiter is null)
                    {
                        _lastWaitingReceive = operation._previousWaiter;
                    }
                    else
                    {
                        operation._nextWaiter._previousWaiter = operation._previousWaiter;
                    }
                    operation._previousWaiter = null;
                    operation._nextWaiter = null;
                    operation._waitingRequest = default;
                    UnlinkOperation(operation);
                }

                internal bool TryResume(out Interop.Sys.IoRingRequest request)
                {
                    if (HasReadyWaiters)
                    {
                        MultishotReceiveOperation operation = _waitingReceives!;
                        request = operation._waitingRequest;
                        RemoveWaiter(operation);
                        return true;
                    }
                    request = default;
                    return false;
                }

                internal void DrainReturns(ushort[] batch)
                {
                    int count = 0;
                    for (int word = 0; word < _returnedBuffers.Length; word++)
                    {
                        if (Volatile.Read(ref _returnedBuffers[word]) == 0)
                        {
                            continue;
                        }

                        ulong returned = (ulong)Interlocked.Exchange(ref _returnedBuffers[word], 0);
                        while (returned != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(returned);
                            returned &= returned - 1;
                            batch[count++] = (ushort)(word * BitsPerReturnWord + bit);
                            if (count == batch.Length)
                            {
                                PublishReturns(batch, count);
                                count = 0;
                            }
                        }
                    }

                    if (count != 0)
                    {
                        PublishReturns(batch, count);
                    }
                }

                private void PublishReturns(ushort[] batch, int count)
                {
                    fixed (ushort* batchPtr = batch)
                    {
                        if (Interop.Sys.IoRingReturnBuffers(_ring.RingHandle, batchPtr, count) != 0)
                        {
                            Environment.FailFast($"io_uring provided-buffer return failed: {Marshal.GetLastPInvokeError()}.");
                        }
                    }
                    AvailableBuffers += count;
                    Debug.Assert(AvailableBuffers <= BufferCount);
                }
            }

            /// <summary>
            /// A single-use <see cref="IMemoryOwner{Byte}"/> over one fixed provided buffer
            /// (see <see cref="ReceiveBufferPool"/>). <see cref="Dispose"/> returns the buffer to its
            /// pool instead of freeing anything - the backing storage is owned by the ring itself.
            /// </summary>
            private sealed unsafe class ReceiveBufferLease : MemoryManager<byte>
            {
                private ReceiveBufferPool? _pool;
                private readonly int _bufferId;
                private readonly byte* _basePointer;
                private readonly int _length;

                public ReceiveBufferLease(ReceiveBufferPool pool, int bufferId, byte* basePointer, int length)
                {
                    _pool = pool;
                    _bufferId = bufferId;
                    _basePointer = basePointer;
                    _length = length;
                }

                public override Span<byte> GetSpan()
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _pool) is null, this);
                    return new Span<byte>(_basePointer, _length);
                }

                public override MemoryHandle Pin(int elementIndex = 0)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _pool) is null, this);
                    if ((uint)elementIndex > (uint)_length)
                    {
                        throw new ArgumentOutOfRangeException(nameof(elementIndex));
                    }

                    return new MemoryHandle(_basePointer + elementIndex);
                }

                public override void Unpin()
                {
                }

                protected override void Dispose(bool disposing) => Interlocked.Exchange(ref _pool, null)?.Return(_bufferId);
            }

            /// <summary>
            /// The <see cref="IoUringOperation"/> behind <see cref="IoUringOperation.CreateReceiveMultishot"/>.
            /// Also an <see cref="IThreadPoolWorkItem"/> in its own right: draining and delivering
            /// completions from <see cref="_pending"/> (rather than each completion carrying its own,
            /// separately-allocated work item, as every other <see cref="IoUringOperation"/> does) is
            /// what lets this type guarantee it only ever has at most one active drainer running - see
            /// <see cref="MultishotReceiveOperation.EnqueueFromIssuer"/> and <see cref="IThreadPoolWorkItem.Execute"/>.
            /// </summary>
            internal sealed class MultishotReceiveOperation : IoUringOperation, IThreadPoolWorkItem
            {
                private Ring _ring = null!;
                private readonly Action<int, IMemoryOwner<byte>?, bool> _onCompleted;
                private bool _isDatagram;
                internal MultishotReceiveOperation? _previousWaiter;
                internal MultishotReceiveOperation? _nextWaiter;
                internal Interop.Sys.IoRingRequest _waitingRequest;

                // Completions reaped by the issuer thread (see EnqueueFromIssuer) but this operation's
                // own drainer (see Execute) has not yet delivered to _onCompleted. This queue has exactly
                // one producer by construction: each logical operation is bound to one fd and is
                // routed to exactly one ring (see GetRing), which in turn has
                // exactly one owning issuer thread. That single-producer guarantee, together with
                // _dispatchRequested only ever allowing one active drainer at a time (see
                // EnqueueFromIssuer), is what delivers every completion to _onCompleted in true arrival
                // order with no per-completion sequence number needed anywhere in this type, unlike every
                // other IoUringOperation's completions, which flow through the generic, order-agnostic
                // EnqueueCompletions/CompletionProcessorWorkItem path instead (see DrainCompletions).
                private readonly SingleProducerSingleConsumerQueue<PendingCompletion> _pending = new();

                // 1 while some worker is already draining (or about to drain) _pending, 0 otherwise - the
                // standard reset-then-recheck single-active-consumer coalescing flag: whoever wins the
                // CAS from 0 to 1 is now responsible for draining every item currently in the queue *and*
                // anything enqueued for the remainder of its own Execute call, only giving up that
                // responsibility once it resets this back to 0 and finds the queue still empty. Unlike
                // Ring.CompletionProcessingRequested (see CompletionProcessorWorkItem.Execute), which
                // resets itself before draining specifically so a second instance *can* run concurrently,
                // this flag must never allow two concurrently active drainers - that would reintroduce
                // the exact same out-of-order delivery this type exists to avoid.
                private int _dispatchRequested;

                public MultishotReceiveOperation(Action<int, IMemoryOwner<byte>?, bool> onCompleted)
                {
                    _onCompleted = onCompleted;
                }

                protected override IoUringRequest Request
                {
                    get
                    {
                        // Begin establishes the binding and rejects overlapping reuse before
                        // request preparation can change the ring or socket semantics.
                        Debug.Assert(_binding is not null);
                        Ring ring = _binding._ring;
                        if (ring.ReceiveBuffers is null)
                        {
                            throw new PlatformNotSupportedException();
                        }
                        int socketType = _binding.GetSocketType();

                        const int DatagramSocketType = 2; // SocketType_SOCK_DGRAM in pal_networking.h.
                        _ring = ring;
                        _isDatagram = socketType == DatagramSocketType;
                        return CreateRequest();
                    }
                }

                private static IoUringRequest CreateRequest()
                {
                    Interop.Sys.IoRingRequest request = default;
                    request.OpCode = Interop.Sys.IoRingOp.RecvMultishot;
                    request.Offset = -1;
                    return new IoUringRequest(in request);
                }

                protected override void OnCompleted(int result, uint flags, long sequence) =>
                    // Never actually reached: this operation's completions are always intercepted and
                    // delivered inline by the issuer thread itself (see DrainCompletions/EnqueueFromIssuer),
                    // never handed to the generic EnqueueCompletions/CompletionProcessorWorkItem path that
                    // is the only caller of this method.
                    throw new UnreachableException();

                /// <summary>
                /// Queues one raw completion for delivery. Called only by this ring's
                /// single issuer thread, directly from <see cref="DrainCompletions"/> - see
                /// <see cref="_pending"/>'s own doc comment for why that single-caller invariant is what
                /// makes this operation's delivery order trivially correct.
                /// </summary>
                internal void EnqueueFromIssuer(int result, uint flags)
                {
                    _pending.Enqueue(new PendingCompletion(result, flags));
                    if (Interlocked.CompareExchange(ref _dispatchRequested, 1, 0) == 0)
                    {
                        ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
                    }
                }

                /// <summary>
                /// Drains and delivers every completion currently in <see cref="_pending"/>, in order,
                /// then gives up this operation's "active drainer" status - unless something was enqueued
                /// in the narrow gap between draining the queue empty and actually giving that status up,
                /// in which case this reclaims it and keeps going, rather than leaving that item
                /// undelivered until some future, unrelated completion happens to schedule a new drainer.
                /// </summary>
                void IThreadPoolWorkItem.Execute()
                {
                    Thread currentThread = Thread.CurrentThread;
                    while (true)
                    {
                        while (_pending.TryDequeue(out PendingCompletion completion))
                        {
                            IMemoryOwner<byte>? buffer = null;
                            if ((completion.Flags & Interop.Sys.IoRingCompletion.Buffer) != 0)
                            {
                                int bufferId = (int)(completion.Flags >> Interop.Sys.IoRingCompletion.BufferShift);
                                if (completion.Result > 0 || (completion.Result == 0 && _isDatagram))
                                {
                                    buffer = _ring.ReceiveBuffers!.Rent(bufferId, completion.Result);
                                }
                                else
                                {
                                    _ring.ReceiveBuffers!.Return(bufferId);
                                }
                            }
                            else if (completion.Result == 0 && _isDatagram)
                            {
                                buffer = EmptyDatagram.Instance;
                            }

                            Deliver(completion.Result, buffer,
                                (completion.Flags & Interop.Sys.IoRingCompletion.More) != 0, currentThread);
                        }

                        // Publish the reset before checking for work so neither side can miss
                        // the other's publication and leave a completion without a drainer.
                        Interlocked.Exchange(ref _dispatchRequested, 0);

                        if (_pending.IsEmpty || Interlocked.CompareExchange(ref _dispatchRequested, 1, 0) != 0)
                        {
                            return;
                        }
                    }
                }

                /// <summary>
                /// Transparently resubmits this same still-alive operation after its previous submission
                /// was terminated by the kernel after delivering data or exhausting its buffer pool
                /// (see <see cref="Deliver"/>).
                /// Reuses this instance under its original binding and cancellation registration.
                /// The caller never observes any interruption, aside from a
                /// pause in received data until the pool has room again.
                /// </summary>
                private bool TryResubmit(out int errorResult)
                {
                    errorResult = -Interop.Sys.ConvertErrorPalToPlatform(Interop.Error.ECANCELED);
                    if (IsCancellationRequested)
                    {
                        return false;
                    }
                    try
                    {
                        EnqueueContinuation(CreateRequest());
                        return true;
                    }
                    catch (ObjectDisposedException)
                    {
                        return false;
                    }
                    catch (OutOfMemoryException)
                    {
                        errorResult = -Interop.Sys.ConvertErrorPalToPlatform(Interop.Error.ENOMEM);
                        return false;
                    }
                }

                /// <summary>
                /// Invokes <see cref="_onCompleted"/>. Only ever called from this operation's own
                /// drainer (see <see cref="IThreadPoolWorkItem.Execute"/>), which guarantees at most one
                /// active caller of this method at a time, dequeuing completions in the exact order this
                /// ring's single issuer thread enqueued them (see <see cref="EnqueueFromIssuer"/>) - so,
                /// unlike the generic <see cref="IoUringOperation.CompleteFromIoUring"/> path, no
                /// completion-sequence bookkeeping is needed here at all to guarantee true arrival order.
                /// </summary>
                private void Deliver(int result, IMemoryOwner<byte>? buffer, bool hasMore, Thread currentThread)
                {
                    if (!hasMore)
                    {
                        if (result > 0 || (result == 0 && _isDatagram))
                        {
                            // CQ pressure can terminate a native submission without reaching EOF.
                            // Transfer the final buffer before rearming, and observe cancellation
                            // or binding disposal performed by that callback.
                            InvokeCallback(result, buffer, hasMore: true, currentThread);
                            buffer = null;
                            if (TryResubmit(out result))
                            {
                                return;
                            }
                        }
                        else if (result < 0 && !IsCancellationRequested &&
                            new Interop.ErrorInfo(-result).Error == Interop.Error.ENOBUFS &&
                            TryResubmit(out result))
                        {
                            // Exhaustion terminates the native submission, not the logical receive.
                            // Consumers return buffers independently of this request's lifetime.
                            return;
                        }

                        CompleteOperation();
                    }

                    InvokeCallback(result, buffer, hasMore, currentThread);
                }

                private void InvokeCallback(int result, IMemoryOwner<byte>? buffer, bool hasMore, Thread currentThread)
                {
                    _onCompleted(result, buffer, hasMore);
                    ExecutionContext.ResetThreadPoolThread(currentThread);
                    currentThread.ResetThreadPoolThread();
                }

                private sealed class EmptyDatagram : IMemoryOwner<byte>
                {
                    internal static readonly EmptyDatagram Instance = new();
                    public Memory<byte> Memory => Memory<byte>.Empty;
                    public void Dispose() { }
                }

                private readonly struct PendingCompletion
                {
                    public readonly int Result;
                    public readonly uint Flags;

                    public PendingCompletion(int result, uint flags)
                    {
                        Result = result;
                        Flags = flags;
                    }
                }
            }

        }
    }
}
