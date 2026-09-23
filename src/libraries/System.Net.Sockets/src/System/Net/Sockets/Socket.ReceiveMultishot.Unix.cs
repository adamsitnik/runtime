// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace System.Net.Sockets;

public partial class Socket
{
    private MultishotReceiveContext? _multishotReceive;

    partial void CreateMultishotReceiveEnumerable(CancellationToken cancellationToken, ref IAsyncEnumerable<IMemoryOwner<byte>>? enumerable)
    {
        if (OperatingSystem.IsLinux() && IoUring.IsSupported)
        {
            enumerable = ReceiveMultishotUnixAsync(cancellationToken);
        }
    }

    partial void CancelMultishotReceive() =>
        Volatile.Read(ref _multishotReceive)?.Stop(new SocketException((int)SocketError.OperationAborted));

    private async IAsyncEnumerable<IMemoryOwner<byte>> ReceiveMultishotUnixAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        MultishotReceiveContext context = new(this, cancellationToken);
        Volatile.Write(ref _multishotReceive, context);
        try
        {
            await using (context.ConfigureAwait(false))
            {
                context.Start();
                while (await context.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (context.TryRead(out IMemoryOwner<byte>? owner))
                    {
                        bool transferred = false;
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            ThrowIfDisposed();
                            transferred = true;
                        }
                        finally
                        {
                            if (!transferred)
                            {
                                owner!.Dispose();
                            }
                        }
                        yield return owner!;
                    }
                }
            }
        }
        finally
        {
            Volatile.Write(ref _multishotReceive, null);
        }

        if (context.Unsupported)
        {
            await foreach (IMemoryOwner<byte> owner in ReceiveRepeatedlyAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return owner;
            }
        }
    }

    private sealed class MultishotReceiveContext : IValueTaskSource<bool>, IAsyncDisposable
    {
        private readonly Socket _socket;
        private readonly CancellationToken _cancellationToken;
        private readonly Action<int, IMemoryOwner<byte>?, bool> _callback;
        private readonly Lock _lock = new();
        private readonly Queue<IMemoryOwner<byte>> _buffers = new();
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ManualResetValueTaskSourceCore<bool> _waiter = new() { RunContinuationsAsynchronously = true };
        private CancellationTokenRegistration _registration;
        private Exception? _error;
        private bool _active;
        private bool _stopping;
        private bool _finished;
        private bool _waiting;
        private bool _receivedData;
        internal bool Unsupported { get; private set; }

        internal MultishotReceiveContext(Socket socket, CancellationToken cancellationToken)
        {
            _socket = socket;
            _cancellationToken = cancellationToken;
            _callback = OnCompleted;
        }

        internal void Start()
        {
            _registration = _cancellationToken.UnsafeRegister(static state =>
            {
                MultishotReceiveContext context = (MultishotReceiveContext)state!;
                context.Stop(new OperationCanceledException(context._cancellationToken));
            }, this);
            lock (_lock)
            {
                if (_stopping)
                {
                    return;
                }
                try
                {
                    _socket.ThrowIfDisposed();
                    _active = IoUring.TrySubmitRecvMultishot(_socket._handle, _callback);
                    Unsupported = !_active;
                }
                catch (ObjectDisposedException exception)
                {
                    _error = exception;
                }
                if (!_active)
                {
                    _finished = true;
                    _closed.TrySetResult();
                }
            }
        }

        private void OnCompleted(int result, IMemoryOwner<byte>? owner, bool more)
        {
            lock (_lock)
            {
                if (owner is not null)
                {
                    if (_stopping)
                    {
                        owner.Dispose();
                    }
                    else
                    {
                        _receivedData = true;
                        _buffers.Enqueue(owner);
                    }
                }
                if (!more)
                {
                    _active = false;
                    _finished = true;
                    if (result < 0 && !_stopping)
                    {
                        Interop.Error error = new Interop.ErrorInfo(-result).Error;
                        if (!_receivedData && error is Interop.Error.EINVAL or Interop.Error.ENOTSUP)
                        {
                            Unsupported = true;
                        }
                        else
                        {
                            SocketError socketError = SocketPal.GetSocketErrorForErrorCode(error);
                            _socket.UpdateStatusAfterSocketError(socketError);
                            _error = new SocketException((int)socketError);
                        }
                    }
                    _closed.TrySetResult();
                }
                CompleteWaiter();
            }
        }

        internal ValueTask<bool> WaitToReadAsync()
        {
            lock (_lock)
            {
                if (_buffers.Count != 0)
                {
                    return new ValueTask<bool>(true);
                }
                if (_error is not null)
                {
                    return ValueTask.FromException<bool>(_error);
                }
                if (_finished || _stopping)
                {
                    return new ValueTask<bool>(false);
                }
                _waiter.Reset();
                _waiting = true;
                return new ValueTask<bool>(this, _waiter.Version);
            }
        }

        internal bool TryRead(out IMemoryOwner<byte>? owner)
        {
            lock (_lock)
            {
                return _buffers.TryDequeue(out owner);
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
                while (_buffers.TryDequeue(out IMemoryOwner<byte>? owner))
                {
                    owner.Dispose();
                }
                if (_active)
                {
                    IoUring.TryCancelRecvMultishot(_socket._handle);
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
            if (_waiting && (_buffers.Count != 0 || _finished || _stopping))
            {
                _waiting = false;
                _waiter.SetResult(true);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Stop(error: null);
            await _registration.DisposeAsync().ConfigureAwait(false);
            await _closed.Task.ConfigureAwait(false);
        }

        bool IValueTaskSource<bool>.GetResult(short token)
        {
            _waiter.GetResult(token);
            lock (_lock)
            {
                if (_error is not null && _buffers.Count == 0)
                {
                    throw _error;
                }
                return _buffers.Count != 0;
            }
        }

        ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _waiter.GetStatus(token);
        void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _waiter.OnCompleted(continuation, state, token, flags);
    }
}
