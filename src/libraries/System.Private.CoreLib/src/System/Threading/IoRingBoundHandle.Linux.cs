// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Ring = System.Threading.PortableThreadPool.IoUringThreadPool.Ring;

namespace System.Threading;

// A binding gives one numeric file descriptor a stable ring and retains one SafeHandle
// while accepted native work can still use that descriptor. Closing an fd alone does not
// cancel io_uring requests: the kernel holds its own file references. Conversely, releasing our
// SafeHandle references too early could recycle the fd while a queued request still contains it.
//
// For example, a pending Socket.ReceiveAsync pins its buffer and calls EnqueueForSubmission on
// the socket's cached binding:
// - GetOrCreate, used on first binding, retains the supplied wrapper before reading its fd.
//   The registry makes aliases of that same fd share this binding, without retaining each alias.
//   If a borrowed wrapper bound first, the owning wrapper replaces it when that owner binds.
//   A different owning wrapper is rejected: one descriptor must not have independent owners.
//   The weak registry does not itself keep abandoned bindings alive. Long weak references keep
//   a finalizer-pending binding discoverable until its references are actually released.
// - EnqueueForSubmissionCore starts one logical operation, including its cancellation registration.
//   EnqueueContinuation then acquires native ownership before publishing anything to the ring.
//   It fills in this binding's fd and creates any native message header. A prepublication failure
//   rolls back that ownership; accepted work is retired by the issuer, not by the submitting thread.
// - The issuer suppresses canceled, unpublished work or submits it and waits for its terminal CQE.
//   ReleaseNative balances AcquireNative at that point. Worker delivery subsequently lets the
//   socket adapter unpin and complete the receive. Native drain does not require that worker to run.
//   Partial sends/writes may enqueue another native request within the same logical operation.
//
// Cancellation and disposal are requests, not reclamation fences. QueueCancellation routes a
// targeted cancel to the stored ring. DisposeCore atomically closes admission and queues cancel-all;
// the issuer walks _operationsHead, its intrusive list of cancelable requests. The low state bits
// count accepted native requests, including queued requests not yet published to the kernel.
// Only their retirement drains the binding; a cancel-command CQE is not sufficient.
//
// OnDrained signals a lazily allocated waiter and queues off-issuer cleanup. DisposeAndWaitCore,
// used by synchronous Socket close, waits only for native drain and can perform that cleanup
// itself, avoiding a dependency on ThreadPool availability or on application callbacks.
// AcquireSend/ReleaseSend additionally track logical sends across the native-drained gap between
// partial completions. HadPendingOperations remembers whether close must remain abortive.
//
// ReleaseReference claims cleanup exactly once, removes this registry entry before fd reuse is
// possible, then releases the retained handle reference outside the
// registry lock and never on the issuer. A custom ReleaseHandle may reenter DisposeAndWait on the
// same thread; _releasingThreadId prevents that thread from waiting for itself, while other callers
// wait for actual release. ExecuteCore performs deferred cleanup; the finalizer covers abandonment.
// A borrowed handle alone cannot protect against its unregistered owner closing the descriptor.
public sealed partial class IoRingBoundHandle
{
    private const int Closed = int.MinValue;
    private const int HadPendingOperations = 1 << 30;
    private const int CountMask = HadPendingOperations - 1;
    // Long weak references keep a finalizing binding discoverable until its handle references
    // are retired, without keeping abandoned owners alive indefinitely.
    private static readonly Dictionary<nint, WeakReference<IoRingBoundHandle>> s_bindings = new();

    private SafeHandle _handle;
    private bool _ownsFileDescriptor;
    private int _state;
    private int _referenceReleased = 2;
    private int _releasingThreadId;
    private int _cleanupQueued;
    private int _pendingSends;
    private ManualResetEventSlim? _drained;

    internal readonly Ring _ring;
    internal readonly nint _fileDescriptor;
    internal IoUringOperation? _operationsHead;
    internal IoRingBoundHandle? _nextClosing;

    private IoRingBoundHandle(SafeHandle handle, nint fileDescriptor, bool ownsFileDescriptor)
    {
        _handle = handle;
        _ownsFileDescriptor = ownsFileDescriptor;
        _fileDescriptor = fileDescriptor;
        _ring = PortableThreadPool.IoUringThreadPool.GetRing(fileDescriptor);
        _referenceReleased = 0;
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

    internal int GetSocketType()
    {
        lock (s_bindings)
        {
            // Promotion can release the old borrowed wrapper. Keep it alive through marshalling.
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            Interop.Error error = Interop.Sys.GetIoRingSocketType(_handle,
                out _, out int socketType, out _, out _);
            if (error != Interop.Error.SUCCESS)
            {
                throw Interop.GetExceptionForIoErrno(new Interop.ErrorInfo(error));
            }
            return socketType;
        }
    }

    internal static IoRingBoundHandle GetOrCreate(SafeHandle handle, bool ownsFileDescriptor)
    {
        handle.DangerousAddRef();
        SafeHandle? handleToRelease = handle;
        IoRingBoundHandle? unpublishedBinding = null;
        try
        {
            nint fileDescriptor = handle.DangerousGetHandle();
            if ((nuint)fileDescriptor > int.MaxValue)
            {
                throw new ArgumentException(SR.Arg_InvalidHandle, nameof(handle));
            }

            lock (s_bindings)
            {
                if (s_bindings.TryGetValue(fileDescriptor, out WeakReference<IoRingBoundHandle>? reference) &&
                    reference.TryGetTarget(out IoRingBoundHandle? binding))
                {
                    ObjectDisposedException.ThrowIf(binding.IsDisposed, handle);
                    if (ownsFileDescriptor)
                    {
                        if (binding._ownsFileDescriptor && !ReferenceEquals(binding._handle, handle))
                        {
                            throw new ArgumentException(SR.Arg_IoUringConflictingHandleOwner, nameof(handle));
                        }

                        if (!binding._ownsFileDescriptor)
                        {
                            // Transfer the acquired reference to the binding before releasing the
                            // borrowed wrapper. Cleanup observes the replacement under this same lock.
                            handleToRelease = binding._handle;
                            binding._handle = handle;
                            binding._ownsFileDescriptor = true;
                        }
                    }
                    return binding;
                }

                unpublishedBinding = new IoRingBoundHandle(handle, fileDescriptor, ownsFileDescriptor);
                handleToRelease = null;
                s_bindings[fileDescriptor] = new WeakReference<IoRingBoundHandle>(unpublishedBinding, trackResurrection: true);
                binding = unpublishedBinding;
                unpublishedBinding = null;
                return binding;
            }
        }
        finally
        {
            handleToRelease?.DangerousRelease();
            unpublishedBinding?.DisposeAndWait();
        }
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
                SafeHandle handle;
                lock (s_bindings)
                {
                    if (s_bindings.TryGetValue(_fileDescriptor, out WeakReference<IoRingBoundHandle>? reference) &&
                        reference.TryGetTarget(out IoRingBoundHandle? binding) && ReferenceEquals(binding, this))
                    {
                        // Native work is drained. Remove before closing can recycle the descriptor,
                        // and never run an arbitrary ReleaseHandle while holding the registry lock.
                        s_bindings.Remove(_fileDescriptor);
                    }
                    handle = _handle;
                }
                handle.DangerousRelease();
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
