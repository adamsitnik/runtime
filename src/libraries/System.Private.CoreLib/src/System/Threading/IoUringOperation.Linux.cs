// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
// Managed request descriptors transfer pin management to this base class. Unsafe descriptors
// retain the caller's explicit native-memory lifetime contract.
//
// ProcessCompletion returns a continuation decision; only the runtime publishes it and completes
// the logical use. TrackSend holds the binding's logical-send count
// across that gap, so close still knows that unsent bytes remain.
//
// On final delivery the runtime calls CompleteOperationCore before notifying the adapter.
// It rejects live native work, disposes the token registration (waiting for callbacks),
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
    private IoUringCompletion _completion;
    private Exception? _completionError;
    private bool _stopping;
    private MemoryHandle _bufferPin;
    private ReadOnlyMemory<byte> _pinnedBuffer;
    private bool _hasBufferPin;
    private bool _providedBufferTaken;
    private SingleProducerSingleConsumerQueue<IoUringCompletion>? _pending;
    private int _dispatchRequested;
    private int _completionReady;
    private int _processingThreadId;
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
    internal IoUringOperation? _previousWaiter;
    internal IoUringOperation? _nextWaiter;
    internal Interop.Sys.IoRingRequest _waitingRequest;

    private bool IsCancellationRequestedCore =>
        (Volatile.Read(ref _state) & CancellationRequested) != 0 || _binding?.IsDisposed == true;

    internal bool CancellationIsRequested => IsCancellationRequested;
    internal bool IsNativePending => Volatile.Read(ref _nativePending) != 0;
    internal bool RequiresOrderedDelivery { get; private set; }
    internal bool UsesProvidedBuffers { get; private set; }
    internal CancellationToken OperationCancellationToken => _registration.Token;

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

    internal IoUringRequest GetRequest() => PrepareRequest();

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
        _completionError = null;
        _stopping = false;
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

    internal unsafe Interop.Sys.IoRingRequest PrepareResources(in IoUringRequest request)
    {
        if (!_tracksSend && request._nativeRequest.OpCode is
            Interop.Sys.IoRingOp.Send or Interop.Sys.IoRingOp.SendMsg or Interop.Sys.IoRingOp.SendZeroCopy)
        {
            TrackSend();
        }
        // A multishot request or SEND_ZC can produce several CQEs before a worker runs.
        // Keep using the queue across resubmissions, including a scalar continuation, so
        // terminal delivery and reuse cannot race a previous issuer's queue publication.
        RequiresOrderedDelivery |= request.RequiresOrderedDelivery;
        if (RequiresOrderedDelivery)
        {
            _pending ??= new SingleProducerSingleConsumerQueue<IoUringCompletion>();
        }
        UsesProvidedBuffers = request._nativeRequest.OpCode == Interop.Sys.IoRingOp.RecvMultishot;
        if (UsesProvidedBuffers && _binding!._ring.ReceiveBuffers is null)
        {
            throw new PlatformNotSupportedException();
        }
        Interop.Sys.IoRingRequest nativeRequest = request._nativeRequest;
        if (request._hasBuffer)
        {
            if (!_hasBufferPin || !_pinnedBuffer.Equals(request._buffer))
            {
                ReleaseBuffer();
                _bufferPin = request._buffer.Pin();
                _pinnedBuffer = request._buffer;
                _hasBufferPin = true;
            }
            nativeRequest.Buffer = (byte*)_bufferPin.Pointer + request._bufferOffset;
        }
        return nativeRequest;
    }

    private void ReleaseBuffer()
    {
        MemoryHandle pin = _bufferPin;
        _bufferPin = default;
        _pinnedBuffer = default;
        _hasBufferPin = false;
        pin.Dispose();
    }

    private Exception? ReleaseResources(Exception? error)
    {
        try
        {
            OnCompleting();
        }
        catch (Exception cleanupError)
        {
            error = error is null ? cleanupError : new AggregateException(error, cleanupError);
        }
        try
        {
            ReleaseBuffer();
        }
        catch (Exception cleanupError)
        {
            error = error is null ? cleanupError : new AggregateException(error, cleanupError);
        }
        return error;
    }

    // Keep native cleanup's P/Invoke frame out of the per-completion hot path.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal unsafe void ReleaseNativeHeader()
    {
        if (_messageHeader != null)
        {
            NativeMemory.Free(_messageHeader);
            _messageHeader = null;
        }
    }

    internal Exception Abandon(Exception submissionError)
    {
        try
        {
            ReleaseNativeHeader();
            return ReleaseResources(submissionError)!;
        }
        finally
        {
            CompleteOperationCore();
        }
    }

    internal IThreadPoolWorkItem? CompleteFromIoUring(in Interop.Sys.IoRingCompletion completion)
    {
        _completion = new IoUringCompletion(completion.Result, completion.Flags, completion.Extra1, completion.Extra2);
        Volatile.Write(ref _completionReady, 1);
        // A scalar continuation can complete while its previous callback's worker is
        // still draining. Publish to that worker instead of starting another drainer.
        return Interlocked.CompareExchange(ref _dispatchRequested, 1, 0) == 0 ? this : null;
    }

    internal void EnqueueFromIssuer(in Interop.Sys.IoRingCompletion completion)
    {
        Debug.Assert(_pending is not null);
        _pending.Enqueue(new IoUringCompletion(completion.Result, completion.Flags, completion.Extra1, completion.Extra2));
        // Enqueue may expose a new segment before updating its producer tail. Only expose a
        // consumable completion after that update, so terminal callbacks can safely change issuers.
        Interlocked.Increment(ref _completionReady);
        if (Interlocked.CompareExchange(ref _dispatchRequested, 1, 0) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }
    }

    private void ExecuteCore()
    {
        Thread currentThread = Thread.CurrentThread;
        Debug.Assert(currentThread.IsThreadPoolThread);

        while (true)
        {
            while (Volatile.Read(ref _completionReady) != 0)
            {
                IoUringCompletion completion;
                if (RequiresOrderedDelivery)
                {
                    Debug.Assert(_pending is not null);
                    bool dequeued = _pending.TryDequeue(out completion);
                    Debug.Assert(dequeued);
                }
                else
                {
                    completion = _completion;
                }
                Interlocked.Decrement(ref _completionReady);
                if (Process(completion))
                {
                    // Final notification may already have reused this instance on a different ring.
                    return;
                }
                ExecutionContext.ResetThreadPoolThread(currentThread);
                currentThread.ResetThreadPoolThread();
            }
            // Relinquish dispatch before rechecking: an issuer racing this handoff either
            // queues another worker or leaves a completion for this worker to reclaim.
            Interlocked.Exchange(ref _dispatchRequested, 0);
            if (Volatile.Read(ref _completionReady) == 0 ||
                Interlocked.CompareExchange(ref _dispatchRequested, 1, 0) != 0)
            {
                return;
            }
        }
    }

    private bool Process(IoUringCompletion completion)
    {
        if (!completion.HasMore)
        {
            ReleaseNativeHeader();
        }

        _completion = completion;
        IoUringCompletionAction action = default;
        try
        {
            if (!_stopping)
            {
                _processingThreadId = Environment.CurrentManagedThreadId;
                action = ProcessCompletion(in completion);
                if (action._kind == 1 && !completion.HasMore ||
                    action._kind == 2 && completion.HasMore)
                {
                    throw new InvalidOperationException(SR.InvalidOperation_IoUringCompletionAction);
                }
                if (action._kind == 0)
                {
                    _stopping = true;
                    _completionError = action._error;
                }
            }
            else
            {
                // Cancellation does not suppress CQEs already produced by the kernel.
                // Let an unsafe operation reclaim its own results without invoking its consumer.
                OnCompletionDiscarded(in completion);
            }
        }
        catch (Exception error)
        {
            _completionError = _completionError is null ? error : new AggregateException(_completionError, error);
            _stopping = true;
        }
        finally
        {
            _processingThreadId = 0;
            if (UsesProvidedBuffers && (completion.Flags & Interop.Sys.IoRingCompletion.Buffer) != 0 &&
                !_providedBufferTaken)
            {
                _binding!._ring.ReceiveBuffers!.Return((int)(completion.Flags >> Interop.Sys.IoRingCompletion.BufferShift));
            }
            _providedBufferTaken = false;
        }

        if (completion.HasMore)
        {
            if (_stopping)
            {
                RequestCancellationCore();
            }
            return false;
        }

        if (!_stopping && action._kind == 2)
        {
            try
            {
                if (IsCancellationRequested)
                {
                    throw new OperationCanceledException(OperationCancellationToken);
                }
                _binding!.EnqueueContinuation(this, in action._request);
                return false;
            }
            catch (Exception error)
            {
                _completionError = error;
            }
        }

        Exception? completionError = ReleaseResources(_completionError);
        _completionError = null;
        Interlocked.Exchange(ref _dispatchRequested, 0);
        if (RequiresOrderedDelivery)
        {
            Thread currentThread = Thread.CurrentThread;
            ExecutionContext.ResetThreadPoolThread(currentThread);
            currentThread.ResetThreadPoolThread();
        }
        CompleteOperationCore();
        OnCompleted(completionError);
        return true;
    }

    private IMemoryOwner<byte>? TakeBufferCore()
    {
        if (_processingThreadId != Environment.CurrentManagedThreadId || _providedBufferTaken)
        {
            throw new InvalidOperationException(SR.InvalidOperation_IoUringBufferRequired);
        }
        if (!UsesProvidedBuffers || (_completion.Flags & Interop.Sys.IoRingCompletion.Buffer) == 0 ||
            _completion.Result < 0)
        {
            return null;
        }
        int id = (int)(_completion.Flags >> Interop.Sys.IoRingCompletion.BufferShift);
        IMemoryOwner<byte> buffer = _binding!._ring.ReceiveBuffers!.Rent(id, _completion.Result);
        _providedBufferTaken = true;
        return buffer;
    }
}
