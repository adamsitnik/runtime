// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Runtime.InteropServices;

namespace System.Threading
{
    /// <summary>
    /// Exposes the experimental io_uring Thread Pool infrastructure to other framework components.
    /// </summary>
    /// <remarks>
    /// This prototype shares sharded rings with <see cref="System.IO.RandomAccess"/> on Linux.
    /// Each ring has one dedicated issuer; completion callbacks run on Thread Pool workers.
    /// The API may change incompatibly or be removed without notice.
    /// </remarks>
    [CLSCompliant(false)]
    public static class IoUring
    {
        /// <summary>
        /// Whether the io_uring Thread Pool integration is enabled and usable on this system. When
        /// <see langword="false"/>, every <c>TrySubmit*</c> method below always returns
        /// <see langword="false"/> and callers should use their normal (non-io_uring) code path.
        /// </summary>
        public static bool IsSupported => PortableThreadPool.IoUringThreadPool.IsEnabled;

        /// <summary>Gets the canonical binding of a handle to one io_uring issuer.</summary>
        /// <param name="handle">The handle to bind without transferring its ownership.</param>
        /// <returns>The shared binding for this handle.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="handle"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="handle"/> is invalid.</exception>
        /// <exception cref="PlatformNotSupportedException">io_uring is unavailable.</exception>
        /// <exception cref="ObjectDisposedException">The handle or its binding has been disposed.</exception>
        public static IoRingBoundHandle Bind(SafeHandle handle)
        {
            ArgumentNullException.ThrowIfNull(handle);
            ObjectDisposedException.ThrowIf(handle.IsClosed || handle.IsDisposeRequested, handle);
            if (handle.IsInvalid)
            {
                // SafeSocketHandle.IsInvalid also observes IsClosed, which can change
                // after the preceding check.
                ObjectDisposedException.ThrowIf(handle.IsClosed || handle.IsDisposeRequested, handle);
                throw new ArgumentException(SR.Arg_InvalidHandle, nameof(handle));
            }
            if (!IsSupported)
            {
                throw new PlatformNotSupportedException();
            }
            return IoRingBoundHandle.GetOrCreate(handle);
        }

        /// <summary>Gets an existing handle binding without creating one or reopening a disposed binding.</summary>
        /// <param name="handle">The handle whose binding to retrieve.</param>
        /// <param name="binding">The existing binding, or <see langword="null"/> if none exists.</param>
        /// <returns><see langword="true"/> if a binding exists; otherwise, <see langword="false"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="handle"/> is null.</exception>
        public static bool TryGetBinding(SafeHandle handle,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IoRingBoundHandle? binding)
        {
            ArgumentNullException.ThrowIfNull(handle);
            binding = null;
            return SafeHandle.s_disposeNotification is not null && IoRingBoundHandle.TryGet(handle, out binding);
        }

        /// <summary>
        /// Attempts to submit a <c>recv(2)</c>-like read of up to <paramref name="length"/> bytes
        /// from <paramref name="handle"/> into <paramref name="buffer"/>. On success (return value
        /// <see langword="true"/>), <paramref name="handle"/> is ref-counted for the duration of the
        /// operation and <paramref name="buffer"/> must remain valid and pinned until
        /// <paramref name="onCompleted"/> is invoked - exactly once, on some Thread Pool worker
        /// thread (never inline on the calling thread, and never on the shared ring's driver
        /// thread - completions are always redispatched as ordinary Thread Pool work items), with
        /// either the number of bytes received (&gt;= 0) or <c>-errno</c> on failure. Returns
        /// <see langword="false"/> if io_uring is unsupported or disabled; no callback is invoked
        /// in that case. Other submission failures throw before accepting the request.
        /// Disposing the handle or its canonical binding requests cancellation, but callers
        /// must still retain their buffers until completion.
        /// </summary>
        public static unsafe bool TrySubmitRecv(SafeHandle handle, byte* buffer, int length, int flags, Action<int> onCompleted) =>
            TrySubmitCore(handle, Interop.Sys.IoRingOp.Recv, buffer, length, flags, null, null, onCompleted);

        /// <summary>
        /// Attempts to submit a <c>send(2)</c>-like write of up to <paramref name="length"/> bytes
        /// from <paramref name="buffer"/> to <paramref name="handle"/>. See
        /// <see cref="TrySubmitRecv"/> for the buffer lifetime and completion contract.
        /// </summary>
        public static unsafe bool TrySubmitSend(SafeHandle handle, byte* buffer, int length, int flags, Action<int> onCompleted) =>
            TrySubmitCore(handle, Interop.Sys.IoRingOp.Send, buffer, length, flags, null, null, onCompleted);

        /// <summary>
        /// Attempts to submit a gather send using native <c>iovec</c> entries.
        /// </summary>
        /// <param name="handle">The socket handle.</param>
        /// <param name="vectors">A pointer to an array of native <c>iovec</c> entries.</param>
        /// <param name="vectorCount">The number of entries in <paramref name="vectors"/>.</param>
        /// <param name="flags">A bitwise combination of native <c>MSG_*</c> flags.</param>
        /// <param name="onCompleted">The callback receiving the byte count or negative errno.</param>
        /// <returns><see langword="true"/> if submitted; otherwise, <see langword="false"/>.</returns>
        /// <remarks>
        /// The vectors and their buffers must remain pinned until the callback, following
        /// <see cref="TrySubmitRecv"/>'s lifetime contract. Uses <c>MSG_WAITALL</c> to retry partial
        /// stream sends in the kernel, but errors and the native vector limit can still produce
        /// a short result. Handle or binding disposal requests cancellation of outstanding sends.
        /// </remarks>
        public static unsafe bool TrySubmitSendV(SafeHandle handle, void* vectors, int vectorCount, int flags, Action<int> onCompleted)
        {
            ArgumentNullException.ThrowIfNull(vectors);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorCount);

            return TrySubmitCore(handle, Interop.Sys.IoRingOp.SendMsg, (byte*)vectors, vectorCount, flags, null, null, onCompleted);
        }

        /// <summary>
        /// Attempts to submit an <c>accept(2)</c>-like operation on the listening socket
        /// <paramref name="handle"/>. On completion, <paramref name="onCompleted"/> is invoked with
        /// either the new connected socket's close-on-exec file descriptor (&gt;= 0) or <c>-errno</c> on failure;
        /// <paramref name="sockAddr"/>/<paramref name="sockAddrLen"/> receive the peer's address, and
        /// must remain valid/pinned until then. See <see cref="TrySubmitRecv"/> for the general
        /// submission/lifetime contract.
        /// </summary>
        public static unsafe bool TrySubmitAccept(SafeHandle handle, byte* sockAddr, int* sockAddrLen, int flags, Action<int> onCompleted) =>
            TrySubmitCore(handle, Interop.Sys.IoRingOp.Accept, null, 0, flags, sockAddr, sockAddrLen, onCompleted);

        /// <summary>
        /// Attempts to submit a <c>connect(2)</c>-like operation on <paramref name="handle"/> toward
        /// the address described by <paramref name="sockAddr"/>/<paramref name="sockAddrLen"/> (an
        /// input-only, by-value length here, unlike <see cref="TrySubmitAccept"/>). On completion,
        /// <paramref name="onCompleted"/> is invoked with <c>0</c> on success or <c>-errno</c> on
        /// failure. See <see cref="TrySubmitRecv"/> for the general submission/lifetime contract.
        /// </summary>
        public static unsafe bool TrySubmitConnect(SafeHandle handle, byte* sockAddr, int* sockAddrLen, Action<int> onCompleted) =>
            TrySubmitCore(handle, Interop.Sys.IoRingOp.Connect, null, 0, 0, sockAddr, sockAddrLen, onCompleted);

        /// <summary>
        /// Attempts to submit a persistent, multishot <c>recv(2)</c>-like read on
        /// <paramref name="handle"/>: a single submission that keeps producing completions - one per
        /// datagram/read the kernel has data for - until cancelled (via <paramref name="operation"/>'s
        /// <see cref="IoUringOperation.RequestCancellation"/>), EOF, or an error occurs, instead of
        /// completing exactly once like <see cref="TrySubmitRecv"/>. Received data is delivered via
        /// kernel-provided buffers leased from a pool, rather than a caller-supplied buffer:
        /// <paramref name="onCompleted"/> is invoked, on some Thread Pool worker thread, once per
        /// completion, with the raw result (bytes received, <c>0</c> on stream EOF or an empty datagram, or <c>-errno</c> on
        /// failure), the received data (dispose it to return the buffer to the pool;
        /// <see langword="null"/> when no data accompanies this completion), and whether the operation is
        /// still alive and will keep producing further completions. Native submissions can be rearmed
        /// transparently; only the logical operation's last callback reports <see langword="false"/>.
        /// Empty datagrams have an empty, non-null buffer and do not end the operation.
        /// If all provided buffers are retained, reception waits until a consumer returns a buffer;
        /// cancellation and handle disposal still stop the waiting operation.
        /// <paramref name="handle"/> is ref-counted while each native submission is in flight.
        /// Returns <see langword="false"/> if the
        /// operation could not be submitted (in which case <paramref name="operation"/> is
        /// <see langword="null"/> and no callback is invoked).
        /// </summary>
        public static bool TrySubmitRecvMultishot(SafeHandle handle, Action<int, IMemoryOwner<byte>?, bool> onCompleted, out IoUringOperation? operation)
        {
            ArgumentNullException.ThrowIfNull(handle);
            ArgumentNullException.ThrowIfNull(onCompleted);

            if (!IsSupported)
            {
                operation = null;
                return false;
            }

            return PortableThreadPool.IoUringThreadPool.TrySubmitReceiveMultishot(handle, onCompleted, out operation);
        }

        private static unsafe bool TrySubmitCore(
            SafeHandle handle,
            Interop.Sys.IoRingOp opCode,
            byte* buffer,
            int length,
            int flags,
            byte* sockAddr,
            int* sockAddrLen,
            Action<int> onCompleted)
        {
            ArgumentNullException.ThrowIfNull(handle);
            ArgumentNullException.ThrowIfNull(onCompleted);

            if (!IsSupported)
            {
                return false;
            }

            Interop.Sys.IoRingRequest request = default;
            request.OpCode = opCode;
            request.Offset = -1;
            request.Buffer = buffer;
            request.BufferLength = length;
            request.Flags = flags;
            request.SockAddr = sockAddr;
            request.SockAddrLen = sockAddrLen;
            ActionIoUringOperation operation = ActionIoUringOperation.Rent(in request, onCompleted);
            bool submitted = false;
            try
            {
                Bind(handle).Enqueue(operation);
                submitted = true;
                return true;
            }
            finally
            {
                if (!submitted)
                {
                    operation.Return();
                }
            }
        }

        /// <summary>
        /// Adapts a plain <see cref="Action{Int32}"/> completion callback to the
        /// <see cref="IoUringOperation"/> contract, so callers of this public API never need to know
        /// about (or implement) that interface. Unlike a per-thread-ring design, the shared-ring driver
        /// never runs continuations inline: this type also implements <see cref="IThreadPoolWorkItem"/>
        /// so it can be returned from <see cref="IoUringOperation.CompleteFromIoUring(int, uint, long)"/>
        /// and queued (possibly batched together with other completions drained in the same pass)
        /// instead of being invoked directly on the driver thread.
        /// </summary>
        private sealed class ActionIoUringOperation : IoUringOperation
        {
            [ThreadStatic]
            private static ActionIoUringOperation? t_cachedOperation;

            private Action<int>? _onCompleted;
            private IoUringRequest _request;

            protected override IoUringRequest Request => _request;

            public static ActionIoUringOperation Rent(in Interop.Sys.IoRingRequest request, Action<int> onCompleted)
            {
                ActionIoUringOperation operation = t_cachedOperation ?? new ActionIoUringOperation();
                t_cachedOperation = null;
                operation._request = new IoUringRequest(in request);
                operation._onCompleted = onCompleted;
                return operation;
            }

            public void Return()
            {
                _request = default;
                _onCompleted = null;
                t_cachedOperation ??= this;
            }

            protected override void OnCompleted(int result, uint flags, long sequence)
            {
                Action<int> onCompleted = _onCompleted!;
                CompleteOperation();
                Return();
                onCompleted(result);
            }
        }
    }
}
