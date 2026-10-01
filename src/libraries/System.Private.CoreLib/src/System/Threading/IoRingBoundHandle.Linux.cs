// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ring = System.Threading.PortableThreadPool.IoUringThreadPool.Ring;

namespace System.Threading;

public sealed partial class IoRingBoundHandle
{
    private const int Closed = int.MinValue;
    private const int HadPendingOperations = 1 << 30;
    private const int CountMask = HadPendingOperations - 1;
    private static readonly ConditionalWeakTable<SafeHandle, IoRingBoundHandle> s_bindings = new();

    private readonly SafeHandle _handle;
    private int _state;
    private int _referenceReleased = 2;
    private int _releasingThreadId;
    private int _cleanupQueued;
    private int _pendingSends;
    private ManualResetEventSlim? _drained;

    internal readonly Ring _ring;
    internal readonly IntPtr _fileDescriptor;
    internal IoUringOperation? _operations;
    internal IoRingBoundHandle? _nextClosing;

    private IoRingBoundHandle(SafeHandle handle)
    {
        bool added = false;
        handle.DangerousAddRef(ref added);
        Debug.Assert(added);
        try
        {
            _handle = handle;
            _fileDescriptor = handle.DangerousGetHandle();
            if ((ulong)_fileDescriptor.ToInt64() > int.MaxValue)
            {
                throw new ArgumentException(SR.Arg_InvalidHandle, nameof(handle));
            }
            _ring = PortableThreadPool.IoUringThreadPool.GetRing(_fileDescriptor);
            _referenceReleased = 0;
        }
        catch
        {
            handle.DangerousRelease();
            throw;
        }
    }

    // Balance the DangerousAddRef even if the owner abandons its handle and binding.
    ~IoRingBoundHandle()
    {
        if (_referenceReleased == 0)
        {
            Dispose();
        }
    }

    internal bool IsDisposed => Volatile.Read(ref _state) < 0;
    internal SafeHandle Handle => _handle;

    internal static IoRingBoundHandle GetOrCreate(SafeHandle handle)
    {
        IoRingBoundHandle binding;
        lock (s_bindings)
        {
            if (!s_bindings.TryGetValue(handle, out binding!))
            {
                ObjectDisposedException.ThrowIf(handle.IsClosed, handle);
                binding = new IoRingBoundHandle(handle);
                try
                {
                    s_bindings.Add(handle, binding);
                }
                catch
                {
                    binding.DisposeAndWait();
                    throw;
                }
            }
        }

        ObjectDisposedException.ThrowIf(binding.IsDisposed, handle);
        return binding;
    }

    private void EnqueueForSubmissionCore(IoUringOperation operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        operation.Begin(this, cancellationToken);
        try
        {
            IoUringRequest request = operation.GetRequest();
            if (request._nativeRequest.OpCode is Interop.Sys.IoRingOp.Send or Interop.Sys.IoRingOp.SendMsg)
            {
                // A partial send can have no native request while its continuation is pending.
                // Track the logical send so closing in that gap still uses abortive close.
                operation.TrackSend();
            }
            EnqueueContinuation(operation, in request._nativeRequest);
        }
        catch
        {
            operation.Abandon();
            throw;
        }
    }

    internal void EnqueueContinuation(IoUringOperation operation, in Interop.Sys.IoRingRequest request)
    {
        AcquireNative();
        bool acquired = false;
        try
        {
            operation.AcquireNative();
            acquired = true;
            Interop.Sys.IoRingRequest nativeRequest = request;
            nativeRequest.Fd = _fileDescriptor;
            operation.PrepareNative(ref nativeRequest);
            PortableThreadPool.IoUringThreadPool.Enqueue(_ring, operation, in nativeRequest);
        }
        catch
        {
            if (acquired)
            {
                operation.RetireNative();
                operation.ReleaseNativeHeader();
            }
            else
            {
                ReleaseNative();
            }
            throw;
        }
    }

    private void AcquireNative()
    {
        int state = Volatile.Read(ref _state);
        while (true)
        {
            ObjectDisposedException.ThrowIf(state < 0, this);
            if (state == CountMask)
            {
                throw new InvalidOperationException(SR.InvalidOperation_AsyncIOInProgress);
            }
            int next = state + 1;
            int observed = Interlocked.CompareExchange(ref _state, next, state);
            if (observed == state)
            {
                return;
            }
            state = observed;
        }
    }

    internal void ReleaseNative()
    {
        int state = Interlocked.Decrement(ref _state);
        if (state < 0 && (state & CountMask) == 0)
        {
            OnDrained();
        }
    }

    internal void QueueCancellation(IoUringOperation operation) =>
        PortableThreadPool.IoUringThreadPool.RequestCancellation(_ring, operation);

    internal void AcquireSend() => Interlocked.Increment(ref _pendingSends);

    internal void ReleaseSend() => Interlocked.Decrement(ref _pendingSends);

    private void DisposeCore()
    {
        int state = Volatile.Read(ref _state);
        while (true)
        {
            if (state < 0)
            {
                return;
            }
            int next = state | Closed;
            // A partial send can be native-drained while its worker still owes a
            // continuation. Closing cancels that logical send and must remain abortive.
            if (state != 0 || Volatile.Read(ref _pendingSends) != 0)
            {
                next |= HadPendingOperations;
            }
            int observed = Interlocked.CompareExchange(ref _state, next, state);
            if (observed == state)
            {
                break;
            }
            state = observed;
        }

        PortableThreadPool.IoUringThreadPool.RequestClose(_ring, this);
        if (state == 0)
        {
            // Idle disposal must release the owner's reference synchronously, for example
            // so closing a file releases its exclusive lock before returning.
            ReleaseReference();
        }
        GC.SuppressFinalize(this);
    }

    private bool DisposeAndWaitCore()
    {
        Dispose();
        if ((Volatile.Read(ref _state) & CountMask) != 0)
        {
            ManualResetEventSlim? drained = Volatile.Read(ref _drained);
            if (drained is null)
            {
                ManualResetEventSlim candidate = new(initialState: false, spinCount: 0);
                drained = Interlocked.CompareExchange(ref _drained, candidate, null);
                if (drained is null)
                {
                    drained = candidate;
                }
                else
                {
                    candidate.Dispose();
                }
            }
            if ((Volatile.Read(ref _state) & CountMask) == 0)
            {
                drained.Set();
            }
            drained.Wait();
        }
        ReleaseReference();
        return (Volatile.Read(ref _state) & HadPendingOperations) != 0;
    }

    private void OnDrained()
    {
        Volatile.Read(ref _drained)?.Set();
        if (Interlocked.Exchange(ref _cleanupQueued, 1) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }
    }

    private void ReleaseReference()
    {
        int state = Volatile.Read(ref _state);
        if (state >= 0 || (state & CountMask) != 0)
        {
            throw new InvalidOperationException(SR.InvalidOperation_AsyncIOInProgress);
        }
        if (Interlocked.CompareExchange(ref _referenceReleased, 1, 0) == 0)
        {
            Volatile.Write(ref _releasingThreadId, Environment.CurrentManagedThreadId);
            try
            {
                _handle.DangerousRelease();
            }
            finally
            {
                Volatile.Write(ref _referenceReleased, 2);
            }
        }
        else
        {
            // A custom ReleaseHandle can reenter disposal on this same thread.
            // Other callers still wait for the actual DangerousRelease to finish.
            if (Volatile.Read(ref _releasingThreadId) == Environment.CurrentManagedThreadId)
            {
                return;
            }
            SpinWait spinner = default;
            while (Volatile.Read(ref _referenceReleased) != 2)
            {
                spinner.SpinOnce();
            }
        }
    }

    private void ExecuteCore() => ReleaseReference();
}
