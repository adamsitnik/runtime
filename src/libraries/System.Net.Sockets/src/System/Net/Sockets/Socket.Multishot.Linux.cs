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
        /// <see cref="System.Threading.IoUring.TrySubmitRecvMultishot"/>. Unlike repeatedly calling
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

            void OnCompleted(int result, IMemoryOwner<byte>? buffer, bool hasMore)
            {
                if (buffer is not null)
                {
                    channel.Writer.TryWrite(buffer);
                }

                if (!hasMore)
                {
                    // result == 0 here means graceful EOF - not an error. A negative result while we
                    // are the ones who requested cancellation is reported as OperationCanceledException
                    // instead of the raw (usually -ECANCELED) SocketException.
                    Exception? error = result >= 0
                        ? null
                        : Volatile.Read(ref cancellationRequested)
                            ? new OperationCanceledException(cancellationToken)
                            : new SocketException((int)SocketPal.GetSocketErrorForErrorCode(new Interop.ErrorInfo(-result).Error));
                    channel.Writer.TryComplete(error);
                }
            }

            if (!System.Threading.IoUring.TrySubmitRecvMultishot(handle, OnCompleted, out System.Threading.IIoUringOperation? operation))
            {
                throw new InvalidOperationException(SR.net_sockets_multishot_not_supported);
            }

            // Skip allocating the callback delegate entirely when the token can never be canceled
            // (e.g. CancellationToken.None) - UnsafeRegister would end up being a no-op internally,
            // but the delegate passed to it is still allocated by the caller regardless.
            using CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
                ? cancellationToken.UnsafeRegister(_ =>
                {
                    Volatile.Write(ref cancellationRequested, true);
                    operation!.RequestCancellation();
                }, null)
                : default;

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
                operation!.RequestCancellation();
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
