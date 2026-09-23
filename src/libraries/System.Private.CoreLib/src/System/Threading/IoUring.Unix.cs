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

        /// <summary>
        /// Attempts to submit a <c>recv(2)</c>-like read of up to <paramref name="length"/> bytes
        /// from <paramref name="handle"/> into <paramref name="buffer"/>. On success (return value
        /// <see langword="true"/>), <paramref name="handle"/> is ref-counted for the duration of the
        /// operation and <paramref name="buffer"/> must remain valid and pinned until
        /// <paramref name="onCompleted"/> is invoked - exactly once, on some Thread Pool worker
        /// thread (never inline on the calling thread, and never on the shared ring's driver
        /// thread - completions are always redispatched as ordinary Thread Pool work items), with
        /// either the number of bytes received (&gt;= 0) or <c>-errno</c> on failure. Returns
        /// <see langword="false"/> if the operation could not be submitted (e.g. io_uring is
        /// unsupported/disabled, or the submission queue is momentarily full); no callback is
        /// invoked in that case and the caller should fall back to its normal code path.
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
        /// Attempts to submit an <c>accept(2)</c>-like operation on the listening socket
        /// <paramref name="handle"/>. On completion, <paramref name="onCompleted"/> is invoked with
        /// either the new connected socket's file descriptor (&gt;= 0) or <c>-errno</c> on failure;
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
        /// datagram/read the kernel has data for - until cancelled (<see cref="TryCancelRecvMultishot"/>),
        /// EOF, or an error occurs, instead of completing exactly once like <see cref="TrySubmitRecv"/>.
        /// Received data is delivered via kernel-provided buffers leased from a pool, rather than a
        /// caller-supplied buffer: <paramref name="onCompleted"/> is invoked, on some Thread Pool worker
        /// thread, once per completion, with the raw result (bytes received, <c>0</c> on graceful EOF, or
        /// <c>-errno</c> on failure), the received data (dispose it to return the buffer to the pool;
        /// <see langword="null"/> when no data accompanies this completion), and whether the operation is
        /// still alive and will keep producing further completions - the last completion for a given
        /// submission always reports <see langword="false"/> here. <paramref name="handle"/> is
        /// ref-counted for as long as the operation remains alive. Returns <see langword="false"/> if the
        /// operation could not be submitted; no callback is invoked in that case.
        /// </summary>
        public static bool TrySubmitRecvMultishot(SafeHandle handle, Action<int, IMemoryOwner<byte>?, bool> onCompleted)
        {
            ArgumentNullException.ThrowIfNull(handle);
            ArgumentNullException.ThrowIfNull(onCompleted);

            return IsSupported && PortableThreadPool.IoUringThreadPool.TrySubmitReceiveMultishot(handle, onCompleted);
        }

        /// <summary>
        /// Attempts to cancel the in-flight <see cref="TrySubmitRecvMultishot"/> operation, if any,
        /// currently registered for <paramref name="handle"/>. This only requests cancellation - the
        /// operation's <c>onCompleted</c> callback still receives one final completion afterwards
        /// (typically <c>-ECANCELED</c>) with its "still alive" flag cleared, the same as any other
        /// terminal outcome. Returns <see langword="false"/> if no such operation is currently registered
        /// for this handle (e.g. it already completed, or was never submitted).
        /// </summary>
        public static bool TryCancelRecvMultishot(SafeHandle handle)
        {
            ArgumentNullException.ThrowIfNull(handle);

            return IsSupported && PortableThreadPool.IoUringThreadPool.TryCancelReceiveMultishot(handle);
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

            ActionIoUringOperation operation = ActionIoUringOperation.Rent(handle, onCompleted);

            bool refAdded = false;
            bool submitted = false;
            try
            {
                handle.DangerousAddRef(ref refAdded);
                Interop.Sys.IoRingRequest request = default;
                request.OpCode = opCode;
                request.Fd = handle.DangerousGetHandle();
                request.Offset = -1;
                request.Buffer = buffer;
                request.BufferLength = length;
                request.Flags = flags;
                request.SockAddr = sockAddr;
                request.SockAddrLen = sockAddrLen;

                submitted = PortableThreadPool.IoUringThreadPool.TrySubmit(operation, in request);
                return submitted;
            }
            finally
            {
                if (!submitted)
                {
                    operation.Return();
                    if (refAdded)
                    {
                        handle.DangerousRelease();
                    }
                }
            }
        }

        /// <summary>
        /// Adapts a plain <see cref="Action{Int32}"/> completion callback to the internal
        /// <see cref="PortableThreadPool.IIoUringOperation"/> contract, so callers of this public
        /// API never need to know about (or implement) that internal-only interface. Unlike a
        /// per-thread-ring design, the shared-ring driver never runs continuations inline: this
        /// type also implements <see cref="IThreadPoolWorkItem"/> so it can be returned from
        /// <see cref="PortableThreadPool.IIoUringOperation.CompleteFromIoUring(int, uint, long)"/> and queued
        /// (possibly batched together with other completions drained in the same pass) instead of
        /// being invoked directly on the driver thread.
        /// </summary>
        private sealed class ActionIoUringOperation : IThreadPoolWorkItem, PortableThreadPool.IIoUringOperation
        {
            [ThreadStatic]
            private static ActionIoUringOperation? t_cachedOperation;

            private SafeHandle? _handle;
            private Action<int>? _onCompleted;
            private int _result;

            public static ActionIoUringOperation Rent(SafeHandle handle, Action<int> onCompleted)
            {
                ActionIoUringOperation operation = t_cachedOperation ?? new ActionIoUringOperation();
                t_cachedOperation = null;
                operation._handle = handle;
                operation._onCompleted = onCompleted;
                return operation;
            }

            public void Return()
            {
                _handle = null;
                _onCompleted = null;
                t_cachedOperation ??= this;
            }

            IThreadPoolWorkItem? PortableThreadPool.IIoUringOperation.CompleteFromIoUring(int result, uint flags, long sequence)
            {
                // Legacy dispatch resolves completions on the issuer, so defer the user callback.
                _result = result;
                return this;
            }

            void IThreadPoolWorkItem.Execute()
            {
                SafeHandle handle = _handle!;
                Action<int> onCompleted = _onCompleted!;
                int result = _result;
                Return();
                handle.DangerousRelease();
                onCompleted(result);
            }
        }
    }
}
