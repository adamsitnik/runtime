// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace System.Net.Sockets;

public partial class Socket
{
    private MultishotAcceptContext? _multishotAccept;

    partial void CreateMultishotAcceptEnumerable(CancellationToken cancellationToken, ref IAsyncEnumerable<Socket>? enumerable)
    {
        if (OperatingSystem.IsLinux() && IoUring.IsSupported)
        {
            enumerable = AcceptMultishotUnixAsync(cancellationToken);
        }
    }

    partial void CancelMultishotAccept() =>
        Volatile.Read(ref _multishotAccept)?.Stop(new SocketException((int)SocketError.OperationAborted));

    private async IAsyncEnumerable<Socket> AcceptMultishotUnixAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MultishotAcceptContext context = new(this, cancellationToken);
        if (Interlocked.CompareExchange(ref _multishotAccept, context, null) is null)
        {
            await using (context.ConfigureAwait(false))
            {
                context.Start();
                while (await context.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (context.TryRead(out SafeSocketHandle? handle))
                    {
                        Socket? accepted = null;
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            ThrowIfDisposed();
                            accepted = CreateMultishotAcceptSocket(handle!);
                        }
                        finally
                        {
                            if (accepted is null)
                            {
                                handle!.Dispose();
                            }
                        }
                        if (accepted is not null)
                        {
                            yield return accepted;
                        }
                    }
                }
            }
        }

        // Unsupported SQE flags are reported asynchronously. Concurrent enumerators can also use
        // ordinary accepts without sharing this enumerator's cancellation or prefetched sockets.
        await foreach (Socket accepted in AcceptRepeatedlyAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return accepted;
        }
    }

    private Socket? CreateMultishotAcceptSocket(SafeSocketHandle handle)
    {
        Socket accepted = CreateAcceptSocket(handle, remoteEP: null);
        bool keepSocket = false;
        try
        {
            // Unlike ordinary accept, multishot cannot share a writable sockaddr between
            // completions. Cache the peer before yielding, while getpeername is still usable.
            _ = accepted.RemoteEndPoint;
            keepSocket = true;
            return accepted;
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.NotConnected or SocketError.ConnectionReset)
        {
            // A prefetched connection reset before it could be handed to the caller.
            if (NetEventSource.Log.IsEnabled())
            {
                NetEventSource.Error(this, exception);
            }
            return null;
        }
        finally
        {
            if (!keepSocket)
            {
                accepted.Dispose();
            }
        }
    }

    private sealed class MultishotAcceptContext : IValueTaskSource<bool>, IAsyncDisposable
    {
        private const int PauseThreshold = 64;
        private const int ResumeThreshold = PauseThreshold / 2;
        private readonly Socket _listener;
        private readonly CancellationToken _cancellationToken;
        private readonly Action<int, bool> _callback;
        private readonly Queue<SafeSocketHandle> _accepted = new();
        private readonly Lock _lock = new();
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ManualResetValueTaskSourceCore<bool> _waiter = new() { RunContinuationsAsynchronously = true };
        private CancellationTokenRegistration _registration;
        private Exception? _error;
        private bool _waiting;
        private bool _active;
        private bool _pausing;
        private bool _stopping;
        private bool _unsupported;
        private bool _receivedSocket;

        internal MultishotAcceptContext(Socket listener, CancellationToken cancellationToken)
        {
            _listener = listener;
            _cancellationToken = cancellationToken;
            _callback = OnCompleted;
        }

        internal void Start()
        {
            _registration = _cancellationToken.UnsafeRegister(static state =>
            {
                MultishotAcceptContext context = (MultishotAcceptContext)state!;
                context.Stop(new OperationCanceledException(context._cancellationToken));
            }, this);
            lock (_lock)
            {
                Arm();
            }
        }

        private void Arm()
        {
            if (_stopping || _unsupported || _error is not null || _active)
            {
                return;
            }
            if (_listener.Disposed)
            {
                _error = new ObjectDisposedException(nameof(Socket));
                return;
            }
            try
            {
                _active = IoUring.TrySubmitAcceptMultishot(_listener._handle, _callback);
                _unsupported = !_active;
            }
            catch (ObjectDisposedException exception)
            {
                _error = exception;
            }
        }

        private void OnCompleted(int result, bool more)
        {
            lock (_lock)
            {
                if (result >= 0)
                {
                    SafeSocketHandle handle = new((IntPtr)result, ownsHandle: true);
                    if (_stopping)
                    {
                        handle.Dispose();
                    }
                    else
                    {
                        _receivedSocket = true;
                        _accepted.Enqueue(handle);
                    }
                }
                else if (!_stopping)
                {
                    Interop.Error error = new Interop.ErrorInfo(-result).Error;
                    if (error == Interop.Error.ECANCELED)
                    {
                        // Either this queue or the runtime's callback queue paused prefetch.
                    }
                    else if (!_receivedSocket && error is Interop.Error.EINVAL or Interop.Error.ENOTSUP)
                    {
                        _unsupported = true;
                    }
                    else if (error is not (Interop.Error.EINTR or Interop.Error.ECONNABORTED))
                    {
                        _error = new SocketException((int)SocketPal.GetSocketErrorForErrorCode(error));
                    }
                }

                if (!more)
                {
                    _active = false;
                    _pausing = false;
                    if (_accepted.Count < PauseThreshold)
                    {
                        Arm();
                    }
                    if (_stopping)
                    {
                        _closed.TrySetResult();
                    }
                }
                else if (!_stopping && !_pausing && _accepted.Count >= PauseThreshold)
                {
                    // Already-produced CQEs still belong to us and are retained until consumed.
                    _pausing = true;
                    IoUring.TryCancelAcceptMultishot(_listener._handle);
                }
                CompleteWaiter();
            }
        }

        internal ValueTask<bool> WaitToReadAsync()
        {
            lock (_lock)
            {
                if (_accepted.Count != 0)
                {
                    return new ValueTask<bool>(true);
                }
                if (_error is not null)
                {
                    return ValueTask.FromException<bool>(_error);
                }
                if (_unsupported || _stopping)
                {
                    return new ValueTask<bool>(false);
                }
                _waiter.Reset();
                _waiting = true;
                return new ValueTask<bool>(this, _waiter.Version);
            }
        }

        internal bool TryRead(out SafeSocketHandle? handle)
        {
            lock (_lock)
            {
                bool found = _accepted.TryDequeue(out handle);
                if (_accepted.Count < ResumeThreshold)
                {
                    Arm();
                }
                return found;
            }
        }

        internal void Stop(Exception? error)
        {
            lock (_lock)
            {
                if (_stopping)
                {
                    return;
                }
                _stopping = true;
                _error ??= error;
                while (_accepted.TryDequeue(out SafeSocketHandle? handle))
                {
                    handle.Dispose();
                }
                if (_active)
                {
                    IoUring.TryCancelAcceptMultishot(_listener._handle);
                }
                else
                {
                    _closed.TrySetResult();
                }
                CompleteWaiter();
            }
        }

        private void CompleteWaiter()
        {
            if (_waiting && (_accepted.Count != 0 || _error is not null || _unsupported || _stopping))
            {
                _waiting = false;
                _waiter.SetResult(true);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Stop(error: null);
            _registration.Dispose();
            await _closed.Task.ConfigureAwait(false);
            Interlocked.CompareExchange(ref _listener._multishotAccept, null, this);
        }

        bool IValueTaskSource<bool>.GetResult(short token)
        {
            _waiter.GetResult(token);
            lock (_lock)
            {
                if (_error is not null)
                {
                    throw _error;
                }
                return !_unsupported && !_stopping;
            }
        }

        ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _waiter.GetStatus(token);
        void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _waiter.OnCompleted(continuation, state, token, flags);
    }
}
