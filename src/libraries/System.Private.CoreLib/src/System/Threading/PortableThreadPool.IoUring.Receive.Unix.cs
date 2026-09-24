// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace System.Threading
{
    internal sealed partial class PortableThreadPool
    {
        internal static partial class IoUringThreadPool
        {
            /// <summary>
            /// Attempts to submit a persistent, multishot <c>recv(2)</c>-like read on
            /// <paramref name="handle"/>, using this fd's ring's provided-buffer pool (see
            /// <see cref="Ring.ReceiveBuffers"/>). Unlike every other <c>TrySubmit*</c> operation in this
            /// type, a single submission here keeps producing completions - one per datagram/read the
            /// kernel has data for - until cancelled (see <paramref name="operation"/>'s
            /// <see cref="IIoUringOperation.RequestCancellation"/>), EOF, or an error occurs.
            /// <paramref name="onCompleted"/> is invoked, on some Thread Pool worker thread, once per
            /// completion, with the raw result (bytes received, 0 on graceful EOF, or <c>-errno</c> on
            /// failure), the received data (as a buffer leased from the pool - dispose it to return it),
            /// and whether the operation is still alive and will keep producing further completions.
            /// <paramref name="handle"/> is ref-counted while each native submission is outstanding.
            /// Returns <see langword="false"/> only if this fd's ring's buffer pool could not be
            /// initialized (extremely unlikely - see the ring-creation handshake in the static
            /// constructor of <see cref="IoUringThreadPool"/>, which requires it to succeed at all); in
            /// that case <paramref name="operation"/> is <see langword="null"/>.
            /// </summary>
            public static bool TrySubmitReceiveMultishot(SafeHandle handle, Action<int, IMemoryOwner<byte>?, bool> onCompleted, out IIoUringOperation? operation)
            {
                Debug.Assert(s_isEnabled);

                bool refAdded = false;
                bool submitted = false;
                try
                {
                    handle.DangerousAddRef(ref refAdded);
                    IntPtr fd = handle.DangerousGetHandle();
                    Ring ring = GetRing(fd);
                    if (ring.ReceiveBuffers is null)
                    {
                        operation = null;
                        return false;
                    }

                    var multishotOperation = new MultishotReceiveOperation(ring, handle, fd, onCompleted);

                    Interop.Sys.IoRingRequest request = default;
                    request.OpCode = Interop.Sys.IoRingOp.RecvMultishot;
                    request.Fd = fd;
                    request.Offset = -1;

                    submitted = TrySubmit(multishotOperation, in request, out ulong userData);
                    Debug.Assert(submitted);

                    // Recorded on the operation itself so a later RequestCancellation call (against this
                    // very instance, held onto directly by the original caller) knows which exact
                    // request to target - see MultishotReceiveOperation.RequestCancellation.
                    multishotOperation.SetInitialUserData(userData);
                    operation = multishotOperation;
                    return true;
                }
                finally
                {
                    if (!submitted && refAdded)
                    {
                        handle.DangerousRelease();
                    }
                }
            }

            /// <summary>
            /// Ring-owned pool of provided buffers (buf_group 0) used by every
            /// <see cref="Interop.Sys.IoRingOp.RecvMultishot"/> request submitted to this ring - see
            /// <see cref="Interop.Sys.IoRingRegisterBufferRing"/>. Backed by natively-allocated,
            /// page-aligned storage that the ring itself owns and frees on close. A completion selects a
            /// buffer by id (0..<see cref="BufferCount"/>-1); <see cref="Rent"/> wraps that buffer's
            /// slice as an <see cref="IMemoryOwner{Byte}"/> without copying, reusing one persistent
            /// <see cref="ReceiveBufferLease"/> per buffer id instead of allocating one per completion -
            /// safe because the kernel never selects the same id again until this pool republishes it
            /// (see <see cref="Return"/>), which only happens after the previous lease for that id has
            /// been disposed.
            /// </summary>
            internal sealed unsafe class ReceiveBufferPool
            {
                public readonly int BufferSize;
                public readonly int BufferCount;

                private readonly Ring _ring;
                private readonly ReceiveBufferLease[] _leases;

                internal ReceiveBufferPool(Ring ring, int bufferSize, int bufferCount, byte* storage)
                {
                    _ring = ring;
                    BufferSize = bufferSize;
                    BufferCount = bufferCount;

                    var leases = new ReceiveBufferLease[bufferCount];
                    for (int i = 0; i < bufferCount; i++)
                    {
                        leases[i] = new ReceiveBufferLease(this, i, storage + (long)i * bufferSize);
                    }
                    _leases = leases;
                }

                public IMemoryOwner<byte> Rent(int bufferId, int length)
                {
                    ReceiveBufferLease lease = _leases[bufferId];
                    lease.SetLength(length);
                    return lease;
                }

                /// <summary>
                /// Queues <paramref name="bufferId"/> to be republished to the kernel - see
                /// <see cref="Ring.PendingBufferReturns"/> and <see cref="DrainReceiveBufferReturns"/>.
                /// May be called from any thread (whichever one disposes the corresponding
                /// <see cref="ReceiveBufferLease"/>).
                /// </summary>
                internal void Return(int bufferId) => _ring.PendingBufferReturns.Enqueue((ushort)bufferId);
            }

            /// <summary>
            /// A persistent, reusable <see cref="IMemoryOwner{Byte}"/> over one fixed provided buffer
            /// (see <see cref="ReceiveBufferPool"/>). <see cref="Dispose"/> returns the buffer to its
            /// pool instead of freeing anything - the backing storage is owned by the ring itself.
            /// </summary>
            private sealed unsafe class ReceiveBufferLease : MemoryManager<byte>
            {
                private readonly ReceiveBufferPool _pool;
                private readonly int _bufferId;
                private readonly byte* _basePointer;
                private int _length;

                public ReceiveBufferLease(ReceiveBufferPool pool, int bufferId, byte* basePointer)
                {
                    _pool = pool;
                    _bufferId = bufferId;
                    _basePointer = basePointer;
                }

                public void SetLength(int length) => _length = length;

                public override Span<byte> GetSpan() => new Span<byte>(_basePointer, _length);

                public override MemoryHandle Pin(int elementIndex = 0)
                {
                    if ((uint)elementIndex > (uint)_length)
                    {
                        throw new ArgumentOutOfRangeException(nameof(elementIndex));
                    }

                    return new MemoryHandle(_basePointer + elementIndex);
                }

                public override void Unpin()
                {
                }

                protected override void Dispose(bool disposing) => _pool.Return(_bufferId);
            }

            /// <summary>
            /// The <see cref="IIoUringOperation"/> behind <see cref="TrySubmitReceiveMultishot"/>.
            /// Also an <see cref="IThreadPoolWorkItem"/> in its own right: draining and delivering
            /// completions from <see cref="_pending"/> (rather than each completion carrying its own,
            /// separately-allocated work item, as every other <see cref="IIoUringOperation"/> does) is
            /// what lets this type guarantee it only ever has at most one active drainer running - see
            /// <see cref="ScheduleDispatch"/> and <see cref="IThreadPoolWorkItem.Execute"/>.
            /// </summary>
            internal sealed class MultishotReceiveOperation : IIoUringOperation, IThreadPoolWorkItem
            {
                private readonly Ring _ring;
                private readonly SafeHandle _handle;
                private readonly IntPtr _fd;
                private readonly Action<int, IMemoryOwner<byte>?, bool> _onCompleted;

                // Completions the issuer thread has decoded (see EnqueueFromIssuer) but this operation's
                // own drainer (see Execute) has not yet delivered to _onCompleted. This queue has exactly
                // one producer by construction: every fd - and so this operation, which is permanently
                // bound to one fd - is routed to exactly one ring (see GetRing), which in turn has
                // exactly one owning issuer thread. That single-producer guarantee, together with
                // _dispatchRequested only ever allowing one active drainer at a time (see
                // ScheduleDispatch), is what delivers every completion to _onCompleted in true arrival
                // order with no per-completion sequence number needed anywhere in this type, unlike every
                // other IIoUringOperation's completions, which flow through the generic, order-agnostic
                // EnqueueCompletions/CompletionProcessorWorkItem path instead (see DrainCompletions).
                private readonly ConcurrentQueue<PendingCompletion> _pending = new();

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

                // The current submission's raw io_uring userData token, read by RequestCancellation
                // (called from an arbitrary external thread) and written by TryResubmit (called from this
                // operation's own drainer - see Execute/Deliver). Both token and cancellation publication
                // use full fences so their subsequent reads cannot both miss the other publication.
                private ulong _userData;

                // Set just before this operation's truly final delivery to _onCompleted (see Deliver) -
                // checked by RequestCancellation so it never bothers submitting a cancel request once
                // this operation has already finished on its own (harmless if it raced and missed that,
                // just a wasted, harmless IORING_OP_ASYNC_CANCEL against a token the kernel no longer
                // recognizes).
                private volatile bool _finished;

                // Set by RequestCancellation before it ever submits the actual cancel request - checked
                // by Deliver so an ENOBUFS auto-rearm (see TryResubmit) can never race ahead of an
                // already-requested cancellation and keep this receive silently alive behind the caller's
                // back.
                private int _cancelRequested;

                public MultishotReceiveOperation(Ring ring, SafeHandle handle, IntPtr fd, Action<int, IMemoryOwner<byte>?, bool> onCompleted)
                {
                    _ring = ring;
                    _handle = handle;
                    _fd = fd;
                    _onCompleted = onCompleted;
                }

                /// <summary>
                /// Records the initial token without overwriting a replacement if the worker
                /// already rearmed before the submitting thread returned.
                /// </summary>
                public void SetInitialUserData(ulong userData) => Interlocked.CompareExchange(ref _userData, userData, 0);

                /// <summary>
                /// Requests best-effort cancellation of this operation's current submission (see
                /// <see cref="IIoUringOperation.RequestCancellation"/>). Marked cancel-requested before the
                /// actual cancel request is even submitted, so it can never lose a race against Deliver's
                /// own ENOBUFS auto-rearm (see TryResubmit): once this is set, any in-flight or future
                /// completion for this operation observes it and will not silently keep the receive alive
                /// behind the caller's back. Safe to call from any thread - unlike every other operation
                /// this callback of this class touches, this one runs on whichever arbitrary thread the
                /// caller (that is holding onto this very instance) chooses to call it from.
                /// </summary>
                public void RequestCancellation()
                {
                    Interlocked.Exchange(ref _cancelRequested, 1);

                    if (_finished)
                    {
                        return;
                    }

                    Interop.Sys.IoRingRequest request = default;
                    request.OpCode = Interop.Sys.IoRingOp.Cancel;
                    // Routes to the same ring the target request itself was routed to (see GetRing); the
                    // kernel does not otherwise use Fd for IORING_OP_ASYNC_CANCEL.
                    request.Fd = _fd;
                    request.Offset = (long)Volatile.Read(ref _userData);

                    TrySubmit(CancelSentinelOperation.Instance, in request);
                }

                IThreadPoolWorkItem? IIoUringOperation.CompleteFromIoUring(int result, uint flags, long sequence) =>
                    // Never actually reached: this operation's completions are always intercepted and
                    // delivered inline by the issuer thread itself (see DrainCompletions/EnqueueFromIssuer),
                    // never handed to the generic EnqueueCompletions/CompletionProcessorWorkItem path that
                    // is the only caller of this method.
                    throw new UnreachableException();

                /// <summary>
                /// Decodes one raw completion and queues it for delivery. Called only by this ring's
                /// single issuer thread, directly from <see cref="DrainCompletions"/> - see
                /// <see cref="_pending"/>'s own doc comment for why that single-caller invariant is what
                /// makes this operation's delivery order trivially correct.
                /// </summary>
                internal void EnqueueFromIssuer(int result, uint flags)
                {
                    bool hasMore = (flags & Interop.Sys.IoRingCompletion.More) != 0;

                    if (!hasMore)
                    {
                        // Socket disposal can run inline in an earlier receive continuation and
                        // wait for this reference. It must not depend on that worker draining again.
                        _handle.DangerousRelease();
                    }

                    IMemoryOwner<byte>? buffer = null;
                    if (result > 0 && (flags & Interop.Sys.IoRingCompletion.Buffer) != 0)
                    {
                        int bufferId = (int)(flags >> Interop.Sys.IoRingCompletion.BufferShift);
                        buffer = _ring.ReceiveBuffers!.Rent(bufferId, result);
                    }

                    _pending.Enqueue(new PendingCompletion(result, buffer, hasMore));
                    ScheduleDispatch();
                }

                private void ScheduleDispatch()
                {
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
                            Deliver(completion.Result, completion.Buffer, completion.HasMore);
                            ExecutionContext.ResetThreadPoolThread(currentThread);
                            currentThread.ResetThreadPoolThread();
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
                /// was terminated by the kernel due to buffer-pool exhaustion (see <see cref="Deliver"/>).
                /// Reuses this instance and acquires a handle reference for the new native submission.
                /// The caller never observes any interruption, aside from a
                /// pause in received data until the pool has room again.
                /// </summary>
                private bool TryResubmit()
                {
                    bool refAdded = false;
                    bool submitted = false;
                    try
                    {
                        try
                        {
                            _handle.DangerousAddRef(ref refAdded);
                        }
                        catch (ObjectDisposedException)
                        {
                            return false;
                        }

                        Interop.Sys.IoRingRequest request = default;
                        request.OpCode = Interop.Sys.IoRingOp.RecvMultishot;
                        request.Fd = _fd;
                        request.Offset = -1;

                        submitted = TrySubmit(this, in request, out ulong userData);
                        if (submitted)
                        {
                            Interlocked.Exchange(ref _userData, userData);
                            // A concurrent cancellation may have targeted the previous token.
                            // After publication it must also cover this replacement submission.
                            if (Volatile.Read(ref _cancelRequested) != 0)
                            {
                                RequestCancellation();
                            }
                        }

                        return submitted;
                    }
                    finally
                    {
                        if (refAdded && !submitted)
                        {
                            _handle.DangerousRelease();
                        }
                    }
                }

                /// <summary>
                /// Invokes <see cref="_onCompleted"/>. Only ever called from this operation's own
                /// drainer (see <see cref="IThreadPoolWorkItem.Execute"/>), which guarantees at most one
                /// active caller of this method at a time, dequeuing completions in the exact order this
                /// ring's single issuer thread enqueued them (see <see cref="EnqueueFromIssuer"/>) - so,
                /// unlike the generic <see cref="IIoUringOperation.CompleteFromIoUring"/> path, no
                /// completion-sequence bookkeeping is needed here at all to guarantee true arrival order.
                /// </summary>
                private void Deliver(int result, IMemoryOwner<byte>? buffer, bool hasMore)
                {
                    // The kernel terminates (rather than pausing) a multishot receive once its ring's
                    // shared provided-buffer pool (see Ring.ReceiveBuffers) is momentarily exhausted -
                    // this is expected under enough concurrent long-lived receives sharing one pool, not
                    // a real error/EOF the caller should ever observe. Buffers keep being returned to the
                    // kernel as consumers dispose them (see ReceiveBufferPool.Return/
                    // DrainReceiveBufferReturns) independently of whether this particular receive is
                    // currently submitted, so the retry only needs to wait for the pool to have room
                    // again, not for anything specific to this fd.
                    if (!hasMore && result < 0 && Volatile.Read(ref _cancelRequested) == 0 &&
                        new Interop.ErrorInfo(-result).Error == Interop.Error.ENOBUFS &&
                        TryResubmit())
                    {
                        return;
                    }

                    if (!hasMore)
                    {
                        _finished = true;
                    }

                    _onCompleted(result, buffer, hasMore);
                }

                private readonly struct PendingCompletion
                {
                    public readonly int Result;
                    public readonly IMemoryOwner<byte>? Buffer;
                    public readonly bool HasMore;

                    public PendingCompletion(int result, IMemoryOwner<byte>? buffer, bool hasMore)
                    {
                        Result = result;
                        Buffer = buffer;
                        HasMore = hasMore;
                    }
                }
            }

            /// <summary>
            /// Shared, stateless <see cref="IIoUringOperation"/> for <see cref="Interop.Sys.IoRingOp.Cancel"/>
            /// requests submitted by <see cref="MultishotReceiveOperation.RequestCancellation"/>: the
            /// cancel request's own completion (typically 0 on success or <c>-ENOENT</c> if the target
            /// already finished) is not meaningful to any caller - the target operation's own final
            /// completion is what actually signals cancellation to its <c>onCompleted</c> callback - so it
            /// is simply discarded here.
            /// </summary>
            private sealed class CancelSentinelOperation : IIoUringOperation
            {
                public static readonly CancelSentinelOperation Instance = new();

                private CancelSentinelOperation()
                {
                }

                IThreadPoolWorkItem? IIoUringOperation.CompleteFromIoUring(int result, uint flags, long sequence) => null;

                void IIoUringOperation.RequestCancellation() => throw new NotImplementedException();
            }
        }
    }
}
