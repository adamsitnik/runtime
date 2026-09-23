// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
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
            /// kernel has data for - until cancelled (<see cref="TryCancelReceiveMultishot"/>), EOF, or an
            /// error occurs. <paramref name="onCompleted"/> is invoked, on some Thread Pool worker thread,
            /// once per completion, with the raw result (bytes received, 0 on graceful EOF, or
            /// <c>-errno</c> on failure), the received data (as a buffer leased from the pool - dispose it
            /// to return it), and whether the operation is still alive and will keep producing further
            /// completions. <paramref name="handle"/> is ref-counted for as long as the operation remains
            /// alive. Returns <see langword="false"/> only if this fd's ring's buffer pool could not be
            /// initialized (extremely unlikely - see the ring-creation handshake in the static
            /// constructor of <see cref="IoUringThreadPool"/>, which requires it to succeed at all).
            /// </summary>
            public static bool TrySubmitReceiveMultishot(SafeHandle handle, Action<int, IMemoryOwner<byte>?, bool> onCompleted)
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
                        return false;
                    }

                    var operation = new MultishotReceiveOperation(ring, handle, fd, onCompleted);

                    Interop.Sys.IoRingRequest request = default;
                    request.OpCode = Interop.Sys.IoRingOp.RecvMultishot;
                    request.Fd = fd;
                    request.Offset = -1;

                    submitted = TrySubmit(operation, in request, out ulong userData);
                    Debug.Assert(submitted);

                    // Recorded so TryCancelReceiveMultishot can find this exact request's token (and the
                    // operation itself) via the same fd -> ring mapping used to submit it, without a
                    // separate registry.
                    ring.ActiveMultishotReceives[fd] = (userData, operation);
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
            /// Attempts to cancel the in-flight <see cref="TrySubmitReceiveMultishot"/> operation, if
            /// any, currently registered for <paramref name="handle"/>'s fd. This only requests
            /// cancellation - the operation's <c>onCompleted</c> callback still receives one final
            /// completion afterwards (typically <c>-ECANCELED</c>) with its "still alive" flag cleared,
            /// the same as any other terminal outcome. Returns <see langword="false"/> if no such
            /// operation is currently registered for this fd (e.g. it already completed, or was never
            /// submitted).
            /// </summary>
            public static bool TryCancelReceiveMultishot(SafeHandle handle)
            {
                Debug.Assert(s_isEnabled);

                bool refAdded = false;
                try
                {
                    handle.DangerousAddRef(ref refAdded);
                    IntPtr fd = handle.DangerousGetHandle();
                    Ring ring = GetRing(fd);
                    if (!ring.ActiveMultishotReceives.TryGetValue(fd, out (ulong UserData, MultishotReceiveOperation Operation) entry))
                    {
                        return false;
                    }

                    Interop.Sys.IoRingRequest request = default;
                    request.OpCode = Interop.Sys.IoRingOp.Cancel;
                    // Routes to the same ring the target request itself was routed to (see GetRing); the
                    // kernel does not otherwise use Fd for IORING_OP_ASYNC_CANCEL.
                    request.Fd = fd;
                    request.Offset = (long)entry.UserData;

                    // Marked before submitting the cancel request itself, so it can never lose a race
                    // against Deliver's own ENOBUFS auto-rearm (see TryResubmit): once this is set, any
                    // in-flight or future completion for this operation observes it and will not silently
                    // keep the receive alive behind the caller's back. Resolved directly from this fd ->
                    // operation mapping - not via the token/slot machinery (see PeekOperationToken) -
                    // since this call runs on an arbitrary thread that holds no reference on entry.UserData's
                    // token, unlike every other caller of that method.
                    entry.Operation.RequestCancellation();

                    return TrySubmit(CancelSentinelOperation.Instance, in request);
                }
                finally
                {
                    if (refAdded)
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
            /// </summary>
            internal sealed class MultishotReceiveOperation : IIoUringOperation
            {
                private readonly Ring _ring;
                private readonly SafeHandle _handle;
                private readonly IntPtr _fd;
                private readonly Action<int, IMemoryOwner<byte>?, bool> _onCompleted;

                // Sequence number of the last completion actually delivered to _onCompleted, or -1 before
                // the first one - see the ordering gate in Deliver. Reset to -1 whenever this operation is
                // transparently re-armed (see TryResubmit), since a fresh submission's token restarts its
                // own sequence numbering from 0 (see RetainOperationToken).
                private long _deliveredThrough = -1;

                // Set by TryCancelReceiveMultishot (see RequestCancellation) before it ever submits the
                // actual cancel request - checked by CompleteFromIoUring so an ENOBUFS auto-rearm (see
                // TryResubmit) can never race ahead of an already-requested cancellation and keep this
                // receive silently alive behind the caller's back.
                private volatile bool _cancelRequested;

                public MultishotReceiveOperation(Ring ring, SafeHandle handle, IntPtr fd, Action<int, IMemoryOwner<byte>?, bool> onCompleted)
                {
                    _ring = ring;
                    _handle = handle;
                    _fd = fd;
                    _onCompleted = onCompleted;
                }

                public void RequestCancellation() => _cancelRequested = true;

                IThreadPoolWorkItem? IIoUringOperation.CompleteFromIoUring(int result, uint flags, long sequence)
                {
                    bool hasMore = (flags & Interop.Sys.IoRingCompletion.More) != 0;

                    IMemoryOwner<byte>? buffer = null;
                    if (result > 0 && (flags & Interop.Sys.IoRingCompletion.Buffer) != 0)
                    {
                        int bufferId = (int)(flags >> Interop.Sys.IoRingCompletion.BufferShift);
                        buffer = _ring.ReceiveBuffers!.Rent(bufferId, result);
                    }

                    // Deciding whether this terminal completion should be transparently re-armed (see
                    // Deliver) is deliberately *not* done here: this can run out of order relative to
                    // this same operation's other completions (see the remark below), so acting on it
                    // here - in particular resetting _deliveredThrough for the new submission - could
                    // race with an earlier-sequenced sibling completion that has not been delivered yet,
                    // corrupting the ordering gate for both the old and new submissions. Deliver only
                    // ever reaches that decision once it has confirmed (via the gate itself) that this is
                    // truly the last one, i.e. every prior completion has already been delivered.

                    // Unlike every other IIoUringOperation, a still-active multishot operation can have
                    // two of its own completions racing onto two different worker threads (see
                    // CompletionProcessorWorkItem.Execute, which can schedule a second, concurrently
                    // running processor for the same ring before the first one has processed anything).
                    // No per-operation mutable scratch state (of the kind ActionIoUringOperation/
                    // ThreadPoolValueTaskSource use for their single, ever-only-one completion) can
                    // safely be shared between two such completions, so each dispatch below is fully
                    // self-contained instead of reusing fields on this operation.
                    ThreadPool.UnsafeQueueUserWorkItem(s_dispatch, new CompletionState(this, result, buffer, hasMore, sequence), preferLocal: false);

                    return null;
                }

                /// <summary>
                /// Transparently resubmits this same still-alive operation after its previous submission
                /// was terminated by the kernel due to buffer-pool exhaustion (see <see cref="Deliver"/>).
                /// Reuses this instance (and its still-held reference on <see cref="_handle"/>) rather
                /// than allocating a new one - the caller never observes any interruption, aside from a
                /// pause in received data until the pool has room again. Only ever called from
                /// <see cref="Deliver"/>, once it has confirmed (via its own ordering gate) that every
                /// completion of the previous submission - including this, its final one - has already
                /// been delivered, so resetting <see cref="_deliveredThrough"/> here for the new
                /// submission's own sequence numbering cannot race with any of them.
                /// </summary>
                private bool TryResubmit()
                {
                    Interop.Sys.IoRingRequest request = default;
                    request.OpCode = Interop.Sys.IoRingOp.RecvMultishot;
                    request.Fd = _fd;
                    request.Offset = -1;

                    Volatile.Write(ref _deliveredThrough, -1);

                    if (!TrySubmit(this, in request, out ulong userData))
                    {
                        return false;
                    }

                    _ring.ActiveMultishotReceives[_fd] = (userData, this);
                    return true;
                }

                /// <summary>
                /// Invokes <see cref="_onCompleted"/>, but only once every completion with a smaller
                /// <paramref name="sequence"/> has already been delivered - since two completions of this
                /// same still-active multishot operation can be processed (see
                /// <see cref="IIoUringOperation.CompleteFromIoUring"/>) by two different workers, in
                /// either order, this is what guarantees the caller always observes them in true arrival
                /// order despite that. Never blocks the calling Thread Pool worker while waiting for its
                /// turn: doing so (e.g. via a spin-wait) can deadlock the whole pool under load, since the
                /// completion this call would be waiting on can itself be sitting undispatched in
                /// <see cref="Ring.CompletionQueue"/> until some worker thread is free to dequeue and
                /// dispatch it - if every worker is instead blocked spinning here, none ever will be. If
                /// it is not yet this completion's turn, this simply gives up the calling thread and
                /// requeues itself to retry later, the same way <see cref="IIoUringOperation.CompleteFromIoUring"/>
                /// dispatches every completion in the first place.
                /// </summary>
                private void Deliver(int result, IMemoryOwner<byte>? buffer, bool hasMore, long sequence)
                {
                    if (Volatile.Read(ref _deliveredThrough) != sequence - 1)
                    {
                        ThreadPool.UnsafeQueueUserWorkItem(s_dispatch, new CompletionState(this, result, buffer, hasMore, sequence), preferLocal: false);
                        return;
                    }

                    // This is genuinely the last completion this token will ever produce (the kernel does
                    // not emit any more after one without More), and - because of the gate check above -
                    // every earlier-sequenced completion of this same token has already been delivered.
                    // Only at this point is it safe to decide to transparently re-arm: see TryResubmit's
                    // own doc comment for why. The kernel terminates (rather than pausing) a multishot
                    // receive once its ring's shared provided-buffer pool (see Ring.ReceiveBuffers) is
                    // momentarily exhausted - this is expected under enough concurrent long-lived receives
                    // sharing one pool, not a real error/EOF the caller should ever observe. Buffers keep
                    // being returned to the kernel as consumers dispose them (see
                    // ReceiveBufferPool.Return/DrainReceiveBufferReturns) independently of whether this
                    // particular receive is currently submitted, so the retry only needs to wait for the
                    // pool to have room again, not for anything specific to this fd.
                    if (!hasMore && result < 0 && !_cancelRequested &&
                        new Interop.ErrorInfo(-result).Error == Interop.Error.ENOBUFS &&
                        TryResubmit())
                    {
                        return;
                    }

                    if (!hasMore)
                    {
                        _ring.ActiveMultishotReceives.TryRemove(_fd, out _);
                    }

                    _onCompleted(result, buffer, hasMore);

                    Volatile.Write(ref _deliveredThrough, sequence);

                    if (!hasMore)
                    {
                        _handle.DangerousRelease();
                    }
                }

                private static readonly Action<CompletionState> s_dispatch =
                    static state => state.Operation.Deliver(state.Result, state.Buffer, state.HasMore, state.Sequence);

                private readonly struct CompletionState
                {
                    public readonly MultishotReceiveOperation Operation;
                    public readonly int Result;
                    public readonly IMemoryOwner<byte>? Buffer;
                    public readonly bool HasMore;
                    public readonly long Sequence;

                    public CompletionState(MultishotReceiveOperation operation, int result, IMemoryOwner<byte>? buffer, bool hasMore, long sequence)
                    {
                        Operation = operation;
                        Result = result;
                        Buffer = buffer;
                        HasMore = hasMore;
                        Sequence = sequence;
                    }
                }
            }

            /// <summary>
            /// Shared, stateless <see cref="IIoUringOperation"/> for <see cref="Interop.Sys.IoRingOp.Cancel"/>
            /// requests submitted by <see cref="TryCancelReceiveMultishot"/>: the cancel request's own
            /// completion (typically 0 on success or <c>-ENOENT</c> if the target already finished) is not
            /// meaningful to any caller - the target operation's own final completion is what actually
            /// signals cancellation to its <c>onCompleted</c> callback - so it is simply discarded here.
            /// </summary>
            private sealed class CancelSentinelOperation : IIoUringOperation
            {
                public static readonly CancelSentinelOperation Instance = new();

                private CancelSentinelOperation()
                {
                }

                IThreadPoolWorkItem? IIoUringOperation.CompleteFromIoUring(int result, uint flags, long sequence) => null;
            }
        }
    }
}
