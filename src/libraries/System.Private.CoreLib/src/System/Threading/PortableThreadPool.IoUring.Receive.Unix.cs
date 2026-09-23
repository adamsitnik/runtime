// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace System.Threading;

internal sealed partial class PortableThreadPool
{
    internal static partial class IoUringThreadPool
    {
        private static readonly Dictionary<SafeHandle, MultishotReceiveOperation> s_receiveOperations = new();

        internal static bool TrySubmitReceiveMultishot(SafeHandle handle, Action<int, IMemoryOwner<byte>?, bool> callback)
        {
            bool addedRef = false;
            try
            {
                handle.DangerousAddRef(ref addedRef);
                IntPtr fd = handle.DangerousGetHandle();
                Ring[] rings = s_rings!;
                Ring ring = rings[(uint)(nuint)fd % (uint)rings.Length];
                if (ring.ReceiveBuffers is null)
                {
                    return false;
                }

                MultishotReceiveOperation operation = new(ring, handle, fd, callback);
                lock (s_receiveOperations)
                {
                    if (!s_receiveOperations.TryAdd(handle, operation))
                    {
                        return false;
                    }
                    try
                    {
                        // Cancellation looks up this registry under the same lock. Do not
                        // expose the operation until its token and first update are ready.
                        operation.Start();
                    }
                    catch
                    {
                        s_receiveOperations.Remove(handle);
                        throw;
                    }
                }
                addedRef = false;
                return true;
            }
            finally
            {
                if (addedRef)
                {
                    handle.DangerousRelease();
                }
            }
        }

        internal static bool TryCancelReceiveMultishot(SafeHandle handle)
        {
            MultishotReceiveOperation? operation;
            lock (s_receiveOperations)
            {
                s_receiveOperations.TryGetValue(handle, out operation);
            }
            return operation?.Cancel() ?? false;
        }

        private sealed class ReceiveBufferPool
        {
            internal const int BufferSize = 4096;
            internal const int BufferCount = 1024;
            private readonly Ring _ring;
            private readonly ConcurrentQueue<ushort> _returns = new();
            private readonly ushort[] _returnBatch = new ushort[MaxRequestsPerSubmitBatch];
            internal byte[] Storage { get; } = GC.AllocateUninitializedArray<byte>(BufferSize * BufferCount, pinned: true);
            private MultishotReceiveOperation? _firstWaiter;
            private MultishotReceiveOperation? _lastWaiter;
            private int _waiterCount;
            private int _waitersToWake;
            internal ulong ReturnVersion { get; private set; }

            private ReceiveBufferPool(Ring ring) => _ring = ring;

            internal static unsafe ReceiveBufferPool? Create(Ring ring)
            {
                ReceiveBufferPool pool = new(ring);
                fixed (byte* storage = pool.Storage)
                {
                    if (Interop.Sys.IoRingRegisterBufferRing(ring.RingHandle, storage, BufferSize, BufferCount) != 0)
                    {
                        int errno = Marshal.GetLastPInvokeError();
                        if (new Interop.ErrorInfo(errno).Error is Interop.Error.EINVAL or Interop.Error.ENOTSUP or Interop.Error.ENOSYS)
                        {
                            return null;
                        }
                        Environment.FailFast($"io_uring provided-buffer registration failed: {errno}.");
                    }
                }
                return pool;
            }

            internal bool HasReturns => !_returns.IsEmpty || _waitersToWake != 0;

            internal void Return(ushort id)
            {
                _returns.Enqueue(id);
                WakeIssuer(_ring);
            }

            internal unsafe void PublishReturns()
            {
                int count = 0;
                while (count < _returnBatch.Length && _returns.TryDequeue(out ushort id))
                {
                    _returnBatch[count++] = id;
                }
                if (count != 0)
                {
                    fixed (ushort* ids = _returnBatch)
                    {
                        if (Interop.Sys.IoRingReturnBuffers(_ring.RingHandle, ids, count) != 0)
                        {
                            Environment.FailFast($"io_uring buffer return failed: {Marshal.GetLastPInvokeError()}.");
                        }
                    }
                    ReturnVersion++;
                    _waitersToWake = _waiterCount;
                }
                // Retry every waiter once per publication, in bounded issuer turns. A canceled
                // or paused head waiter must not consume the sole wakeup for a returned buffer.
                int wakeBudget = Math.Min(_waitersToWake, MaxRequestsPerSubmitBatch);
                while (wakeBudget-- > 0 && _firstWaiter is { } first)
                {
                    _waitersToWake--;
                    RemoveWaiter(first);
                    first.BuffersAvailable();
                }
                if (_firstWaiter is null)
                {
                    _waitersToWake = 0;
                }
            }

            internal void AddWaiter(MultishotReceiveOperation operation)
            {
                Debug.Assert(!operation._waitingForBuffers);
                _waiterCount++;
                operation._waitingForBuffers = true;
                operation._previousWaiter = _lastWaiter;
                if (_lastWaiter is not null)
                {
                    _lastWaiter._nextWaiter = operation;
                }
                else
                {
                    _firstWaiter = operation;
                }
                _lastWaiter = operation;
            }

            internal void RemoveWaiter(MultishotReceiveOperation operation)
            {
                Debug.Assert(operation._waitingForBuffers);
                _waiterCount--;
                if (operation._previousWaiter is { } previous)
                {
                    previous._nextWaiter = operation._nextWaiter;
                }
                else
                {
                    _firstWaiter = operation._nextWaiter;
                }
                if (operation._nextWaiter is { } next)
                {
                    next._previousWaiter = operation._previousWaiter;
                }
                else
                {
                    _lastWaiter = operation._previousWaiter;
                }
                operation._previousWaiter = null;
                operation._nextWaiter = null;
                operation._waitingForBuffers = false;
            }
        }

        private sealed class ReceiveBufferLease : IMemoryOwner<byte>
        {
            private MultishotReceiveOperation? _operation;
            private readonly ushort _id;
            private readonly int _length;

            internal ReceiveBufferLease(MultishotReceiveOperation operation, ushort id, int length)
            {
                _operation = operation;
                _id = id;
                _length = length;
            }

            public Memory<byte> Memory
            {
                get
                {
                    MultishotReceiveOperation? operation = Volatile.Read(ref _operation);
                    ObjectDisposedException.ThrowIf(operation is null, this);
                    return operation.Pool.Storage.AsMemory(_id * ReceiveBufferPool.BufferSize, _length);
                }
            }

            public void Dispose() => Interlocked.Exchange(ref _operation, null)?.ReturnBuffer(_id);
        }

        // Native state is issuer-owned. Other threads only queue updates and return leases.
        private sealed class MultishotReceiveOperation : IMultishotOperation, IThreadPoolWorkItem
        {
            private const int PauseThreshold = 64;
            private const int ResumeThreshold = PauseThreshold / 2;
            private readonly Ring _ring;
            private readonly SafeHandle _handle;
            private readonly IntPtr _fd;
            private readonly Action<int, IMemoryOwner<byte>?, bool> _callback;
            private readonly ConcurrentQueue<Interop.Sys.IoRingCompletion> _completions = new();
            internal MultishotReceiveOperation? _previousWaiter;
            internal MultishotReceiveOperation? _nextWaiter;
            internal bool _waitingForBuffers;
            private GCHandle _token;
            private ulong _userData;
            private ulong _submissionReturnVersion;
            private bool _queued;
            private bool _running;
            private bool _cancelPending;
            private bool _cancelIssued;
            private bool _pressurePaused;
            private bool _handleReleased;
            private bool _closed;
            private bool _stopRequested;
            private int _terminalResult;
            private int _cancelCompleted;
            private int _updateQueued;
            private int _processing;
            private int _outstandingBuffers;
            internal ReceiveBufferPool Pool => _ring.ReceiveBuffers!;

            internal MultishotReceiveOperation(Ring ring, SafeHandle handle, IntPtr fd, Action<int, IMemoryOwner<byte>?, bool> callback)
            {
                _ring = ring;
                _handle = handle;
                _fd = fd;
                _callback = callback;
                _terminalResult = -new Interop.ErrorInfo(Interop.Error.ECANCELED).RawErrno;
            }

            internal void Start()
            {
                _token = GCHandle.Alloc(this);
                _userData = (ulong)(nuint)GCHandle.ToIntPtr(_token) | MultishotOperationTag;
                try
                {
                    ScheduleUpdate();
                }
                catch
                {
                    _token.Free();
                    throw;
                }
            }

            internal bool Cancel()
            {
                if (Volatile.Read(ref _closed))
                {
                    return false;
                }
                Volatile.Write(ref _stopRequested, true);
                ScheduleUpdate();
                return true;
            }

            private void ScheduleUpdate()
            {
                if (Interlocked.Exchange(ref _updateQueued, 1) == 0)
                {
                    _ring.ReceiveUpdates.Enqueue(this);
                    WakeIssuer(_ring);
                }
            }

            internal void ProcessUpdate()
            {
                Volatile.Write(ref _updateQueued, 0);
                Update();
            }

            private void Update()
            {
                if (_closed)
                {
                    return;
                }
                if (Interlocked.Exchange(ref _cancelCompleted, 0) != 0)
                {
                    _cancelPending = false;
                }
                int outstanding = Volatile.Read(ref _outstandingBuffers);
                if (outstanding >= PauseThreshold)
                {
                    _pressurePaused = true;
                }
                else if (outstanding < ResumeThreshold)
                {
                    _pressurePaused = false;
                }

                bool stopping = Volatile.Read(ref _stopRequested);
                if (stopping && !_running && !_queued && !_handleReleased)
                {
                    // A synchronous Socket.Dispose must not depend on the cancel CQE's worker.
                    _handleReleased = true;
                    _handle.DangerousRelease();
                }
                if (stopping || _pressurePaused)
                {
                    if (_running && !_cancelIssued)
                    {
                        _cancelIssued = true;
                        _cancelPending = true;
                        Interop.Sys.IoRingRequest cancel = new()
                        {
                            OpCode = Interop.Sys.IoRingOp.Cancel,
                            Fd = -1,
                            Offset = unchecked((long)_userData)
                        };
                        QueueOperation(_ring, new ReceiveCancelOperation(this), in cancel, cancellation: true);
                    }
                    if (stopping && !_running && !_queued && !_cancelPending)
                    {
                        if (_waitingForBuffers)
                        {
                            Pool.RemoveWaiter(this);
                        }
                        Volatile.Write(ref _closed, true);
                        lock (s_receiveOperations)
                        {
                            Debug.Assert(s_receiveOperations[_handle] == this);
                            s_receiveOperations.Remove(_handle);
                        }
                        _token.Free();
                        EnqueueCompletion(new Interop.Sys.IoRingCompletion { Result = _terminalResult });
                    }
                    return;
                }
                if (!_running && !_queued && !_cancelPending && !_waitingForBuffers)
                {
                    _queued = true;
                    _ring.PendingSubmissions.Enqueue(new Interop.Sys.IoRingRequest
                    {
                        OpCode = Interop.Sys.IoRingOp.RecvMultishot,
                        Fd = _fd,
                        UserData = _userData
                    });
                }
            }

            public bool TryBeginSubmission()
            {
                _queued = false;
                if (Volatile.Read(ref _stopRequested) || _pressurePaused)
                {
                    return false;
                }
                _running = true;
                _cancelIssued = false;
                _submissionReturnVersion = Pool.ReturnVersion;
                return true;
            }

            public void OnCompletion(Interop.Sys.IoRingCompletion completion)
            {
                bool more = (completion.Flags & Interop.Sys.IoRingCompletion.More) != 0;
                if (!more)
                {
                    _running = false;
                }
                if ((completion.Flags & Interop.Sys.IoRingCompletion.Buffer) != 0)
                {
                    ushort id = (ushort)(completion.Flags >> Interop.Sys.IoRingCompletion.BufferShift);
                    if (id >= ReceiveBufferPool.BufferCount || completion.Result > ReceiveBufferPool.BufferSize)
                    {
                        Environment.FailFast("Invalid io_uring receive buffer.");
                    }
                    if (completion.Result > 0)
                    {
                        Interlocked.Increment(ref _outstandingBuffers);
                        completion.Flags |= Interop.Sys.IoRingCompletion.More;
                        EnqueueCompletion(completion);
                    }
                    else
                    {
                        Pool.Return(id);
                    }
                }
                else if (completion.Result > 0)
                {
                    Environment.FailFast("io_uring receive completed without a selected buffer.");
                }

                if (!more && completion.Result <= 0)
                {
                    Interop.Error error = completion.Result == 0 ? Interop.Error.SUCCESS : new Interop.ErrorInfo(-completion.Result).Error;
                    if (error == Interop.Error.ENOBUFS)
                    {
                        // A return published after this submission may already have made room.
                        // Otherwise wait for a ring-wide return, not for this connection alone.
                        if (Pool.ReturnVersion == _submissionReturnVersion && !_waitingForBuffers)
                        {
                            Pool.AddWaiter(this);
                        }
                    }
                    else if (error != Interop.Error.ECANCELED && error != Interop.Error.EINTR)
                    {
                        _terminalResult = completion.Result;
                        Volatile.Write(ref _stopRequested, true);
                    }
                }
                Update();
            }

            internal void BuffersAvailable()
            {
                Debug.Assert(!_waitingForBuffers);
                Update();
            }

            internal void ReturnBuffer(ushort id)
            {
                Pool.Return(id);
                if (Interlocked.Decrement(ref _outstandingBuffers) == ResumeThreshold - 1)
                {
                    ScheduleUpdate();
                }
            }

            private void EnqueueCompletion(Interop.Sys.IoRingCompletion completion)
            {
                _completions.Enqueue(completion);
                ScheduleCallbacks();
            }

            private void ScheduleCallbacks()
            {
                if (Interlocked.CompareExchange(ref _processing, 1, 0) == 0)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
                }
            }

            void IThreadPoolWorkItem.Execute()
            {
                Thread thread = Thread.CurrentThread;
                int processed = 0;
                while (processed < MaxCompletionsPerTurn && _completions.TryDequeue(out Interop.Sys.IoRingCompletion completion))
                {
                    if (processed++ != 0)
                    {
                        ThreadPool.NotifyWorkItemProgress();
                    }
                    IMemoryOwner<byte>? owner = completion.Result > 0
                        ? new ReceiveBufferLease(this, (ushort)(completion.Flags >> Interop.Sys.IoRingCompletion.BufferShift), completion.Result)
                        : null;
                    _callback(completion.Result, owner, (completion.Flags & Interop.Sys.IoRingCompletion.More) != 0);
                    ExecutionContext.ResetThreadPoolThread(thread);
                    thread.ResetThreadPoolThread();
                }
                Volatile.Write(ref _processing, 0);
                if (!_completions.IsEmpty)
                {
                    ScheduleCallbacks();
                }
            }

            private sealed class ReceiveCancelOperation(MultishotReceiveOperation target) : IIoUringOperation
            {
                public IThreadPoolWorkItem? CompleteFromIoUring(int result)
                {
                    if (result < 0 && new Interop.ErrorInfo(-result).Error is not (Interop.Error.ENOENT or Interop.Error.EALREADY))
                    {
                        Environment.FailFast($"io_uring receive cancellation failed: {-result}.");
                    }
                    Volatile.Write(ref target._cancelCompleted, 1);
                    target.ScheduleUpdate();
                    return null;
                }
            }
        }
    }
}
