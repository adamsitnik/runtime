// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace System.Threading;

// One instance represents a reusable logical operation, not just one SQE. For example, a socket
// send pins its buffer once, begins one logical use, and can enqueue several native sends as
// partial completions advance its offset. Cancellation must cover the whole use, while each
// native request has its own token and retirement fence. Reuse must not let an old cancellation
// or an intrusive control-list link affect the next use, possibly on another binding/ring.
//
// Begin atomically sets Active and advances the generation, rejecting concurrent use. Preparing
// protects initialization of the binding and token registration: an already-canceled token can
// invoke RequestCancellation immediately, before Begin has finished. RequestCancellationCore
// sets CancellationRequested but defers publishing that control record until Preparing clears.
// Its generation check also prevents an in-progress cancellation attempt from crossing reuse.
// Binding disposal is observed as cancellation even without an individual token registration.
//
// PublishingCancellation covers the interval between setting the cancellation bit and linking
// the control record. PublishCancellation queues it on the binding's original ring. The issuer
// owns _previous/_next, _linked and _published; _nextCancellation/_cancellationQueued independently
// track its cancellation-control record. Unpublished canceled work can be suppressed; published
// work needs an exact-token cancel and its own terminal CQE. Neither requesting cancellation nor
// completing the cancel command proves that the kernel has stopped accessing the buffers.
//
// AcquireNative/RetireNative enforce at most one native request at a time and balance the binding's
// accepted-request count. Native retirement occurs on the issuer before worker delivery, so
// synchronous close need not wait for a blocked worker. PrepareNative owns SENDMSG's native
// header until retirement; ReleaseNativeHeader frees it on worker delivery or unpublished rollback.
// The adapter, not this base class, owns the actual data pins and application-visible completion.
//
// CompleteFromIoUring records the result/flags/sequence for worker delivery. ExecuteCore releases
// the retired header and calls the adapter's OnCompleted. A partial send can use
// EnqueueContinuationCore to publish another native request without ending the logical use or
// replacing its cancellation registration. TrackSend holds the binding's logical-send count
// across that gap, so close still knows that unsent bytes remain.
//
// On final delivery the adapter calls CompleteOperationCore before returning this object to its
// cache. It rejects live native work, disposes the token registration (waiting for callbacks),
// waits for a cancellation publisher, sets Completing to prevent new publishers, and waits for
// the issuer to retire any queued control record. Only then can it release logical-send tracking,
// clear the binding and clear the state bits. The generation survives reuse. Abandon follows the
// same logical cleanup when initial enqueue failed before publication, without a completion callback.
public abstract partial class IoUringOperation
{
    private const long Active = 1;
    private const long CancellationRequested = 2;
    private const long PublishingCancellation = 4;
    private const long Completing = 8;
    private const long Preparing = 16;
    private const long GenerationIncrement = 32;
    private const long StateMask = GenerationIncrement - 1;

    private long _state;
    private int _nativePending;
    private CancellationTokenRegistration _registration;
    private int _result;
    private uint _flags;
    private long _sequence;
    private bool _tracksSend;
    private unsafe byte* _messageHeader;

    internal IoRingBoundHandle? _binding;
    internal ulong _nativeToken;
    internal bool _published;
    internal bool _linked;
    internal IoUringOperation? _previous;
    internal IoUringOperation? _next;
    internal IoUringOperation? _nextCancellation;
    internal int _cancellationQueued;

    private bool IsCancellationRequestedCore =>
        (Volatile.Read(ref _state) & CancellationRequested) != 0 || _binding?.IsDisposed == true;

    internal bool CancellationIsRequested => IsCancellationRequested;
    internal bool IsNativePending => Volatile.Read(ref _nativePending) != 0;

    private void RequestCancellationCore()
    {
        long state = Volatile.Read(ref _state);
        long generation = state & ~StateMask;
        while ((state & (Active | CancellationRequested | Completing)) == Active &&
            (state & ~StateMask) == generation)
        {
            long next = state | CancellationRequested;
            if ((state & Preparing) == 0)
            {
                next |= PublishingCancellation;
            }
            long observed = Interlocked.CompareExchange(ref _state, next, state);
            if (observed == state)
            {
                if ((state & Preparing) == 0)
                {
                    PublishCancellation();
                }
                return;
            }
            state = observed;
        }
    }

    private void PublishCancellation()
    {
        _binding!.QueueCancellation(this);
        Interlocked.And(ref _state, ~PublishingCancellation);
    }

    private void CompleteOperationCore()
    {
        if (IsNativePending)
        {
            throw new InvalidOperationException(SR.InvalidOperation_AsyncIOInProgress);
        }
        _registration.Dispose();
        _registration = default;
        SpinWait spinner = default;
        while (true)
        {
            long state = Volatile.Read(ref _state);
            if ((state & PublishingCancellation) == 0 &&
                Interlocked.CompareExchange(ref _state, state | Completing, state) == state)
            {
                break;
            }
            spinner.SpinOnce();
        }

        // A queued control record uses this instance's intrusive linkage. Retire it
        // before another logical use can move the instance to a different ring.
        while (Volatile.Read(ref _cancellationQueued) != 0)
        {
            spinner.SpinOnce();
        }
        if (_tracksSend)
        {
            _binding!.ReleaseSend();
            _tracksSend = false;
        }
        _binding = null;
        Interlocked.And(ref _state, ~StateMask);
    }

    private void EnqueueContinuationCore(IoUringRequest request)
    {
        Debug.Assert(_binding is not null);
        _binding.EnqueueContinuation(this, in request._nativeRequest);
    }

    internal IoUringRequest GetRequest() => Request;

    internal void TrackSend()
    {
        _binding!.AcquireSend();
        _tracksSend = true;
    }

    internal void Begin(IoRingBoundHandle binding, CancellationToken cancellationToken)
    {
        long state = Volatile.Read(ref _state);
        if ((state & Active) != 0 ||
            Interlocked.CompareExchange(ref _state, checked(state + GenerationIncrement) | Active | Preparing, state) != state)
        {
            throw new InvalidOperationException(SR.InvalidOperation_AsyncIOInProgress);
        }

        _binding = binding;
        try
        {
            if (cancellationToken.CanBeCanceled)
            {
                _registration = cancellationToken.UnsafeRegister(
                    static operation => ((IoUringOperation)operation!).RequestCancellation(), this);
            }
        }
        catch
        {
            _binding = null;
            Interlocked.And(ref _state, ~StateMask);
            throw;
        }

        while (true)
        {
            state = Volatile.Read(ref _state);
            long next = state & ~Preparing;
            if ((state & CancellationRequested) != 0)
            {
                next |= PublishingCancellation;
            }
            if (Interlocked.CompareExchange(ref _state, next, state) == state)
            {
                if ((next & PublishingCancellation) != 0)
                {
                    PublishCancellation();
                }
                break;
            }
        }
    }

    internal void AcquireNative()
    {
        if (Interlocked.CompareExchange(ref _nativePending, 1, 0) != 0)
        {
            throw new InvalidOperationException(SR.InvalidOperation_AsyncIOInProgress);
        }
    }

    internal void RetireNative()
    {
        Debug.Assert(IsNativePending);
        _published = false;
        Volatile.Write(ref _nativePending, 0);
        _binding!.ReleaseNative();
    }

    internal unsafe void PrepareNative(ref Interop.Sys.IoRingRequest request)
    {
        Debug.Assert(_messageHeader == null);
        if (request.OpCode == Interop.Sys.IoRingOp.SendMsg)
        {
            _messageHeader = Interop.Sys.IoRingCreateSendMessage(request.Fd,
                (Interop.Sys.IOVector*)request.Buffer, request.BufferLength);
            if (_messageHeader == null)
            {
                throw new OutOfMemoryException();
            }
            request.Buffer = _messageHeader;
        }
    }

    internal unsafe void ReleaseNativeHeader()
    {
        if (_messageHeader != null)
        {
            NativeMemory.Free(_messageHeader);
            _messageHeader = null;
        }
    }

    internal void Abandon()
    {
        ReleaseNativeHeader();
        CompleteOperation();
    }

    internal IThreadPoolWorkItem CompleteFromIoUring(int result, uint flags, long sequence)
    {
        _result = result;
        _flags = flags;
        _sequence = sequence;
        return this;
    }

    private void ExecuteCore()
    {
        int result = _result;
        uint flags = _flags;
        long sequence = _sequence;
        ReleaseNativeHeader();
        OnCompleted(result, flags, sequence);
    }
}
