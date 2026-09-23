// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace System.Threading;

internal sealed partial class PortableThreadPool
{
    internal static partial class IoUringThreadPool
    {
        // Normal handles are user-space addresses; slot generations reserve this bit as well.
        private const ulong MultishotOperationTag = 1UL << 63;
        private const uint OperationSlotGenerationMask = 0x7FFFFFFF;
        private static readonly Dictionary<SafeHandle, MultishotAcceptOperation> s_acceptOperations = new();

        internal static bool TrySubmitAcceptMultishot(SafeHandle handle, Action<int, bool> callback)
        {
            bool addedRef = false;
            try
            {
                handle.DangerousAddRef(ref addedRef);
                IntPtr fd = handle.DangerousGetHandle();
                Ring[] rings = s_rings!;
                Ring ring = rings[(uint)(nuint)fd % (uint)rings.Length];
                MultishotAcceptOperation operation = new(ring, handle, callback);
                lock (s_acceptOperations)
                {
                    if (!s_acceptOperations.TryAdd(handle, operation))
                    {
                        return false;
                    }
                }

                try
                {
                    operation.Submit(fd);
                }
                catch
                {
                    lock (s_acceptOperations)
                    {
                        s_acceptOperations.Remove(handle);
                    }
                    throw;
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

        internal static bool TryCancelAcceptMultishot(SafeHandle handle)
        {
            MultishotAcceptOperation? operation;
            lock (s_acceptOperations)
            {
                s_acceptOperations.TryGetValue(handle, out operation);
            }
            return operation?.Cancel() ?? false;
        }

        private sealed class MultishotAcceptOperation : IThreadPoolWorkItem, IMultishotOperation
        {
            private const int MaxPendingCompletions = 64;
            private readonly Ring _ring;
            private readonly SafeHandle _handle;
            private readonly Action<int, bool> _callback;
            private readonly ConcurrentQueue<Interop.Sys.IoRingCompletion> _completions = new();
            private readonly Lock _lock = new();
            private GCHandle _token;
            private ulong _userData;
            private bool _submitted;
            private bool _cancelRequested;
            private bool _cancelPending;
            private bool _terminal;
            private int _processing;
            private int _pendingCompletions;

            internal MultishotAcceptOperation(Ring ring, SafeHandle handle, Action<int, bool> callback)
            {
                _ring = ring;
                _handle = handle;
                _callback = callback;
            }

            internal void Submit(IntPtr fd)
            {
                lock (_lock)
                {
                    _token = GCHandle.Alloc(this);
                    _userData = (ulong)(nuint)GCHandle.ToIntPtr(_token) | MultishotOperationTag;
                    try
                    {
                        _ring.PendingSubmissions.Enqueue(new Interop.Sys.IoRingRequest
                        {
                            OpCode = Interop.Sys.IoRingOp.AcceptMultishot,
                            Fd = fd,
                            UserData = _userData
                        });
                    }
                    catch
                    {
                        _terminal = true;
                        _token.Free();
                        throw;
                    }
                }
                WakeIssuer(_ring);
            }

            public bool TryBeginSubmission()
            {
                lock (_lock)
                {
                    if (_cancelRequested)
                    {
                        return false;
                    }
                    _submitted = true;
                    return true;
                }
            }

            internal bool Cancel()
            {
                lock (_lock)
                {
                    if (_terminal)
                    {
                        return false;
                    }
                    if (!_cancelRequested)
                    {
                        _cancelRequested = true;
                        if (_submitted)
                        {
                            QueueCancel();
                        }
                    }
                    return true;
                }
            }

            private void QueueCancel()
            {
                _cancelPending = true;
                // The issuer has taken the target out of the pending queue. It publishes that
                // batch before processing these prioritized cancellation requests.
                Interop.Sys.IoRingRequest request = new()
                {
                    OpCode = Interop.Sys.IoRingOp.Cancel,
                    Fd = -1,
                    Offset = unchecked((long)_userData)
                };
                try
                {
                    QueueOperation(_ring, new CancelOperation(this), in request, cancellation: true);
                }
                catch
                {
                    _cancelPending = false;
                    _cancelRequested = false;
                    throw;
                }
            }

            public void OnCompletion(Interop.Sys.IoRingCompletion completion)
            {
                bool terminal = (completion.Flags & Interop.Sys.IoRingCompletion.More) == 0;
                if (terminal)
                {
                    lock (_lock)
                    {
                        _terminal = true;
                        if (!_cancelPending)
                        {
                            _token.Free();
                        }
                    }
                    // Socket.Dispose may synchronously wait for this reference. Never make its
                    // release depend on a ThreadPool callback (or release it under our lock).
                    _handle.DangerousRelease();
                }
                int pending = Interlocked.Increment(ref _pendingCompletions);
                _completions.Enqueue(completion);
                if (!terminal && pending >= MaxPendingCompletions)
                {
                    // Stop prefetch even if workers are busy. The consumer can rearm after
                    // draining this submission; cancellation also drains already-produced CQEs.
                    Cancel();
                }
                Schedule();
            }

            private void Schedule()
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
                    Interlocked.Decrement(ref _pendingCompletions);
                    if (processed++ != 0)
                    {
                        ThreadPool.NotifyWorkItemProgress();
                    }
                    bool more = (completion.Flags & Interop.Sys.IoRingCompletion.More) != 0;
                    if (!more)
                    {
                        lock (s_acceptOperations)
                        {
                            Debug.Assert(s_acceptOperations[_handle] == this);
                            s_acceptOperations.Remove(_handle);
                        }
                    }
                    _callback(completion.Result, more);
                    ExecutionContext.ResetThreadPoolThread(thread);
                    thread.ResetThreadPoolThread();
                }
                Volatile.Write(ref _processing, 0);
                if (!_completions.IsEmpty)
                {
                    Schedule();
                }
            }

            private sealed class CancelOperation(MultishotAcceptOperation target) : IIoUringOperation
            {
                public IThreadPoolWorkItem? CompleteFromIoUring(int result)
                {
                    if (result < 0)
                    {
                        Interop.Error error = new Interop.ErrorInfo(-result).Error;
                        if (error is not (Interop.Error.ENOENT or Interop.Error.EALREADY))
                        {
                            Environment.FailFast($"io_uring accept cancellation failed: {-result}.");
                        }
                    }
                    lock (target._lock)
                    {
                        target._cancelPending = false;
                        if (target._terminal)
                        {
                            target._token.Free();
                        }
                    }
                    return null;
                }
            }
        }
    }
}
