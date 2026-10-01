// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace System.Threading;

/// <summary>Specifies an operation supported by the experimental io_uring integration.</summary>
public enum IoUringOperationKind
{
    /// <summary>Reads a file into one buffer.</summary>
    Read = 0,
    /// <summary>Writes one buffer to a file.</summary>
    Write = 1,
    /// <summary>Reads a file into native iovec entries.</summary>
    ReadScatter = 2,
    /// <summary>Writes native iovec entries to a file.</summary>
    WriteGather = 3,
    /// <summary>Accepts a socket connection.</summary>
    Accept = 4,
    /// <summary>Connects a socket.</summary>
    Connect = 5,
    /// <summary>Receives socket data.</summary>
    Receive = 6,
    /// <summary>Sends socket data.</summary>
    Send = 7,
    /// <summary>Sends socket data from native iovec entries.</summary>
    SendGather = 10,
    /// <summary>Waits for a descriptor to become readable.</summary>
    PollRead = 11,
    /// <summary>Waits for a descriptor to become writable.</summary>
    PollWrite = 12,
}

/// <summary>Describes an experimental io_uring request without its descriptor or correlation identity.</summary>
/// <remarks>
/// All pointed-to storage must remain valid until the operation receives its terminal completion.
/// Requesting cancellation or disposing the binding does not end that lifetime.
/// </remarks>
[CLSCompliant(false)]
public readonly unsafe struct IoUringRequest
{
    internal readonly Interop.Sys.IoRingRequest _nativeRequest;

    /// <summary>Initializes a new instance of the <see cref="IoUringRequest"/> struct.</summary>
    /// <param name="kind">One of the enumeration values that specifies the operation.</param>
    /// <param name="buffer">A pointer to the buffer, or to native iovec entries for a vectored operation.</param>
    /// <param name="length">The buffer length, or the number of iovec entries.</param>
    /// <param name="offset">The file offset, or -1 for non-positional operations.</param>
    /// <param name="flags">The native socket flags.</param>
    /// <param name="socketAddress">A pointer to the native socket address.</param>
    /// <param name="socketAddressLength">A pointer to the native socket address length.</param>
    /// <exception cref="ArgumentOutOfRangeException">The operation, length, or offset is invalid.</exception>
    public IoUringRequest(IoUringOperationKind kind, void* buffer, int length, long offset = -1,
        int flags = 0, void* socketAddress = null, int* socketAddressLength = null)
    {
        if (kind is < IoUringOperationKind.Read or > IoUringOperationKind.Send &&
            kind is < IoUringOperationKind.SendGather or > IoUringOperationKind.PollWrite)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(offset, -1);
        if (kind is IoUringOperationKind.ReadScatter or IoUringOperationKind.WriteGather or IoUringOperationKind.SendGather)
        {
            ArgumentOutOfRangeException.ThrowIfZero(length);
            ArgumentNullException.ThrowIfNull(buffer);
        }

        _nativeRequest.OpCode = (Interop.Sys.IoRingOp)kind;
        _nativeRequest.Offset = offset;
        _nativeRequest.Buffer = (byte*)buffer;
        _nativeRequest.BufferLength = length;
        _nativeRequest.Flags = flags;
        _nativeRequest.SockAddr = (byte*)socketAddress;
        _nativeRequest.SockAddrLen = socketAddressLength;
        if (kind is IoUringOperationKind.ReadScatter or IoUringOperationKind.WriteGather)
        {
            _nativeRequest.Vectors = (Interop.Sys.IOVector*)buffer;
            _nativeRequest.VectorCount = length;
        }
    }

    internal IoUringRequest(in Interop.Sys.IoRingRequest request) => _nativeRequest = request;
}

/// <summary>Represents a reusable experimental io_uring operation with asynchronous cancellation.</summary>
/// <remarks>
/// An instance supports one logical operation at a time. Derived implementations retain their
/// buffers until terminal completion, then call <see cref="CompleteOperation"/> before notifying
/// their caller or caching the instance. Completion callbacks run on ThreadPool workers.
/// </remarks>
[CLSCompliant(false)]
public abstract class IoUringOperation : IThreadPoolWorkItem
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

    /// <summary>Initializes a new instance of the <see cref="IoUringOperation"/> class.</summary>
    protected IoUringOperation()
    {
    }

    /// <summary>Gets the request to enqueue for this operation.</summary>
    protected abstract IoUringRequest Request { get; }

    /// <summary>Gets a value that indicates whether cancellation or binding disposal was requested.</summary>
    protected bool IsCancellationRequested =>
        (Volatile.Read(ref _state) & CancellationRequested) != 0 || _binding?.IsDisposed == true;

    internal bool CancellationIsRequested => IsCancellationRequested;
    internal bool IsNativePending => Volatile.Read(ref _nativePending) != 0;

    /// <summary>Requests cancellation of the current logical operation without waiting for completion.</summary>
    /// <remarks>
    /// The request can race successful completion. It never releases buffers or revokes delivered data.
    /// Do not retain this method as a cancellation capability after the instance has been reused.
    /// </remarks>
    public void RequestCancellation()
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

    /// <summary>Receives a completion on a ThreadPool worker.</summary>
    /// <param name="result">The native result or negative errno.</param>
    /// <param name="flags">The native completion flags.</param>
    /// <param name="sequence">The completion's delivery sequence.</param>
    protected abstract void OnCompleted(int result, uint flags, long sequence);

    /// <summary>Completes the logical operation and permits the instance to be reused.</summary>
    /// <remarks>
    /// Call this only after native completion and before publishing the final result to the caller.
    /// This method waits for an executing cancellation-token callback, not for native I/O.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A native request is still pending.</exception>
    protected void CompleteOperation()
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

    /// <summary>Enqueues a continuation of the same logical operation after native completion.</summary>
    /// <param name="request">The request for the remaining operation.</param>
    /// <remarks>The original cancellation registration remains active across the continuation.</remarks>
    protected void EnqueueContinuation(IoUringRequest request)
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

    void IThreadPoolWorkItem.Execute()
    {
        int result = _result;
        uint flags = _flags;
        long sequence = _sequence;
        ReleaseNativeHeader();
        OnCompleted(result, flags, sequence);
    }
}
