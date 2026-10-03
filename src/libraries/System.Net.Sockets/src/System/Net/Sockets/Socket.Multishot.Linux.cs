// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace System.Net.Sockets
{
    public partial class Socket
    {
        /// <summary>
        /// Streams received data as an <see cref="IAsyncEnumerable{T}"/>, backed by a single persistent
        /// multishot io_uring receive submitted for the enumeration - see
        /// <see cref="IoUringReceiveOperation"/>. Unlike repeatedly calling
        /// <see cref="ReceiveAsync(Memory{byte}, CancellationToken)"/> in a loop, there is normally one
        /// submission for as long as the caller keeps enumerating: the kernel delivers data into
        /// its own pool of buffers as it arrives, without this socket needing to re-arm a new read after
        /// each one. Native submissions terminated by buffer exhaustion or completion-queue pressure are rearmed.
        /// Each yielded <see cref="IMemoryOwner{Byte}"/> must be disposed once the caller is
        /// done with it - this returns its buffer to the pool so the kernel can reuse it. Enumeration
        /// ends (without an exception) on graceful peer shutdown; stopping enumeration early (e.g.
        /// <c>break</c>, or disposing the enumerator) or triggering <paramref name="cancellationToken"/>
        /// requests cancellation of the underlying receive.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The io_uring Thread Pool integration is unavailable on this system (see
        /// <see cref="System.Threading.IoUring.IsSupported"/>).
        /// </exception>
        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        public IAsyncEnumerable<IMemoryOwner<byte>> ReceiveMultishotAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (!System.Threading.IoUring.IsSupported)
            {
                throw new InvalidOperationException(SR.net_sockets_multishot_not_supported);
            }

            return ReceiveMultishotAsyncCore(cancellationToken);
        }

        /// <summary>Delivers multishot receive buffers directly to a callback on a Thread Pool worker.</summary>
        /// <param name="onReceived">The ordered, nonconcurrent callback that takes ownership of each buffer.</param>
        /// <param name="cancellationToken">The token that requests cancellation of the receive operation.</param>
        /// <returns>A task that completes after the receive operation has stopped delivering callbacks.</returns>
        /// <remarks>
        /// The callback must dispose each buffer when finished with it, even if it throws.
        /// Callback exceptions cancel the receive and fault the task after the operation drains.
        /// Cancellation does not revoke buffers already transferred to the callback.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="onReceived"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The socket has been disposed.</exception>
        /// <exception cref="InvalidOperationException">The io_uring integration is unavailable.</exception>
        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        public Task ReceiveMultishotAsync(Action<IMemoryOwner<byte>> onReceived, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(onReceived);
            ThrowIfDisposed();
            if (!IoUring.IsSupported)
            {
                throw new InvalidOperationException(SR.net_sockets_multishot_not_supported);
            }

            return ReceiveMultishotCallbacksAsync(onReceived, cancellationToken);
        }

        private async Task ReceiveMultishotCallbacksAsync(Action<IMemoryOwner<byte>> onReceived, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception? callbackError = null;
            CallbackReceiveOperation operation = new(OnNext, OnCompleted);
            _handle.IoUringBinding.EnqueueForSubmission(operation, cancellationToken);
            await completed.Task.ConfigureAwait(false);

            void OnNext(IMemoryOwner<byte> buffer)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    buffer.Dispose();
                }
                else
                {
                    try
                    {
                        onReceived(buffer);
                    }
                    catch (Exception error)
                    {
                        callbackError = error;
                        throw;
                    }
                }
            }

            void OnCompleted(Exception? error)
            {
                if (callbackError is not null)
                {
                    completed.TrySetException(callbackError);
                }
                else if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    completed.TrySetCanceled(cancellationToken);
                }
                else if (error is not null)
                {
                    completed.TrySetException(error);
                }
                else
                {
                    completed.TrySetResult();
                }
            }
        }

        private sealed class CallbackReceiveOperation : IoUringReceiveOperation
        {
            private readonly Action<IMemoryOwner<byte>> _onNext;
            private readonly Action<Exception?> _onCompleted;

            public CallbackReceiveOperation(Action<IMemoryOwner<byte>> onNext, Action<Exception?> onCompleted)
            {
                _onNext = onNext;
                _onCompleted = onCompleted;
            }

            protected override void OnNext(IMemoryOwner<byte> result) => _onNext(result);

            protected override Exception CreateException(int errorCode) =>
                new SocketException((int)SocketPal.GetSocketErrorForErrorCode(new Interop.ErrorInfo(errorCode).Error));

            protected override void OnCompleted(Exception? error) => _onCompleted(error);
        }

        private async IAsyncEnumerable<IMemoryOwner<byte>> ReceiveMultishotAsyncCore([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            SafeSocketHandle handle = _handle;
            bool cancellationRequested = false;

            // Completion callbacks have one active worker drainer, and enumeration has one consumer.
            // Inline continuations avoid another worker hop; the finite provided-buffer pool bounds
            // how many leases can be queued even though the channel itself is unbounded.
            Channel<IMemoryOwner<byte>> channel = Channel.CreateUnbounded<IMemoryOwner<byte>>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = true,
            });

            void OnNext(IMemoryOwner<byte> buffer)
            {
                if (!channel.Writer.TryWrite(buffer))
                {
                    buffer.Dispose();
                }
            }

            void OnCompleted(Exception? error)
            {
                if (error is OperationCanceledException &&
                    (Volatile.Read(ref cancellationRequested) || cancellationToken.IsCancellationRequested))
                {
                    error = new OperationCanceledException(cancellationToken);
                }
                channel.Writer.TryComplete(error);
            }

            CallbackReceiveOperation operation = new(OnNext, OnCompleted);
            handle.IoUringBinding.EnqueueForSubmission(operation, cancellationToken);

            try
            {
                while (await channel.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    while (channel.Reader.TryRead(out IMemoryOwner<byte>? buffer))
                    {
                        yield return buffer;
                    }
                }
            }
            finally
            {
                // The caller may stop enumerating (break, or dispose the enumerator) while the
                // underlying receive is still active - request its cancellation so the kernel
                // eventually stops producing completions for it instead of leaking an in-flight
                // multishot receive. A no-op if it already completed on its own.
                Volatile.Write(ref cancellationRequested, true);
                operation.RequestCancellation();
                try
                {
                    // Cancellation is asynchronous. Keep returning unyielded leases until the
                    // terminal callback, including completions queued behind this continuation.
                    while (await channel.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        while (channel.Reader.TryRead(out IMemoryOwner<byte>? leftover))
                        {
                            leftover.Dispose();
                        }
                    }
                }
                catch (OperationCanceledException) when (Volatile.Read(ref cancellationRequested))
                {
                }
            }
        }
    }
}
