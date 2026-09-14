// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace System.Threading
{
    /// <summary>
    /// EXPERIMENTAL, PROTOTYPE-ONLY API. Exposes a minimal subset of the Thread Pool's per-thread
    /// io_uring integration (used internally by <see cref="System.IO.RandomAccess"/> on Linux) to
    /// other framework assemblies (e.g. System.Net.Sockets), so a socket completion can be
    /// submitted onto - and reaped from - the *same* per-thread ring a Thread Pool worker already
    /// owns for file I/O, instead of requiring a second, dedicated ring per subsystem. This type
    /// only exists to support an in-progress experiment and may change incompatibly, or be removed
    /// entirely, in a future build without notice.
    /// </summary>
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
        /// Attempts to drain (and run, inline, on the calling thread) any completions already
        /// available on the calling thread's own per-thread ring, if it has one. This exists so a
        /// thread that is busy-waiting for some condition that can only become true once a
        /// completion this same thread owns has been processed (e.g. a Dispose-time
        /// shutdown()/close() sequence waiting for an in-flight io_uring operation on a socket being
        /// closed to observe the shutdown and complete) can make progress instead of deadlocking:
        /// per the per-thread-ring design, only the owning thread can ever drain its own ring, and it
        /// otherwise only does so from the Thread Pool dispatch loop right before parking - which
        /// never happens if that very thread is instead stuck busy-waiting on a queued work item.
        /// Returns <see langword="false"/> if this thread has no ring, or has nothing in flight -
        /// callers should treat that identically to "nothing to do right now" and continue their own
        /// wait/retry loop unchanged.
        /// </summary>
        public static bool TryDriveCurrentThreadRing() => PortableThreadPool.IoUringThreadPool.TryDriveOwnRing();

        /// <summary>
        /// Attempts to submit a <c>recv(2)</c>-like read of up to <paramref name="length"/> bytes
        /// from <paramref name="handle"/> into <paramref name="buffer"/>. On success (return value
        /// <see langword="true"/>), <paramref name="handle"/> is ref-counted for the duration of the
        /// operation and <paramref name="buffer"/> must remain valid and pinned until
        /// <paramref name="onCompleted"/> is invoked - exactly once, on some Thread Pool worker
        /// thread, with either the number of bytes received (&gt;= 0) or <c>-errno</c> on failure.
        /// Returns <see langword="false"/> if the operation could not be submitted (e.g. io_uring is
        /// unsupported/disabled, the calling thread is not eligible to submit, or the submission
        /// queue is momentarily full); no callback is invoked in that case and the caller should fall
        /// back to its normal code path.
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

            bool refAdded = false;
            handle.DangerousAddRef(ref refAdded);
            if (!refAdded)
            {
                return false;
            }

            var operation = new ActionIoUringOperation(handle, onCompleted);

            Interop.Sys.IoRingRequest request = default;
            request.OpCode = opCode;
            request.Fd = handle.DangerousGetHandle();
            request.Offset = -1;
            request.Buffer = buffer;
            request.BufferLength = length;
            request.Flags = flags;
            request.SockAddr = sockAddr;
            request.SockAddrLen = sockAddrLen;

            if (PortableThreadPool.IoUringThreadPool.TrySubmit(operation, in request))
            {
                return true;
            }

            handle.DangerousRelease();
            return false;
        }

        /// <summary>
        /// Adapts a plain <see cref="Action{Int32}"/> completion callback to the internal
        /// <see cref="PortableThreadPool.IIoUringOperation"/> contract, so callers of this public
        /// API never need to know about (or implement) that internal-only interface.
        /// </summary>
        private sealed class ActionIoUringOperation : PortableThreadPool.IIoUringOperation
        {
            private readonly SafeHandle _handle;
            private readonly Action<int> _onCompleted;

            public ActionIoUringOperation(SafeHandle handle, Action<int> onCompleted)
            {
                _handle = handle;
                _onCompleted = onCompleted;
            }

            IThreadPoolWorkItem? PortableThreadPool.IIoUringOperation.CompleteFromIoUring(int result)
            {
                _handle.DangerousRelease();
                _onCompleted(result);
                return null;
            }
        }
    }
}
