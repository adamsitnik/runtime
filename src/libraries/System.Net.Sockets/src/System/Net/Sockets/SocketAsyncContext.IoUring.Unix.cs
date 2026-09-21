// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Threading;

namespace System.Net.Sockets
{
    // Experimental completion-based socket operations over shared, sharded io_uring rings.
    // Each ring has one issuer; callbacks run on Thread Pool workers. Returning false leaves
    // the caller to use the existing SocketAsyncEngine path. This prototype does not yet
    // integrate the existing operation queues' cancellation and close bookkeeping.
    internal sealed partial class SocketAsyncContext
    {
        private IoUringReceiveOperation? _cachedIoUringReceiveOperation;

        /// <summary>
        /// Attempts to complete a plain, single-buffer, no-destination-address Receive via io_uring
        /// instead of registering the socket for epoll-based readiness notification. Returns
        /// <see langword="true"/> if the operation was submitted - <paramref name="callback"/> will be
        /// invoked exactly once, later, with the final result (bytes received, or a mapped
        /// <see cref="SocketError"/> on failure). Returns <see langword="false"/> if the fast path does
        /// not apply or submission failed; the caller must fall back to its normal code path and no
        /// callback will ever be invoked for this attempt.
        /// </summary>
        private unsafe bool TryReceiveViaIoUring(Memory<byte> buffer, SocketFlags flags, Action<int, Memory<byte>, SocketFlags, SocketError> callback)
        {
            if (!System.Threading.IoUring.IsSupported || flags != SocketFlags.None || buffer.Length == 0)
            {
                return false;
            }

            IoUringReceiveOperation operation = Interlocked.Exchange(ref _cachedIoUringReceiveOperation, null)
                ?? new IoUringReceiveOperation(this);
            return operation.TrySubmit(buffer, callback);
        }

        private sealed class IoUringReceiveOperation
        {
            private readonly SocketAsyncContext _context;
            private readonly Action<int> _onCompleted;
            private MemoryHandle _pin;
            private Action<int, Memory<byte>, SocketFlags, SocketError>? _callback;

            public IoUringReceiveOperation(SocketAsyncContext context)
            {
                _context = context;
                _onCompleted = Complete;
            }

            public unsafe bool TrySubmit(Memory<byte> buffer, Action<int, Memory<byte>, SocketFlags, SocketError> callback)
            {
                bool submitted = false;
                try
                {
                    _pin = buffer.Pin();
                    _callback = callback;
                    submitted = System.Threading.IoUring.TrySubmitRecv(
                        _context._socket, (byte*)_pin.Pointer, buffer.Length, 0, _onCompleted);
                    return submitted;
                }
                finally
                {
                    if (!submitted)
                    {
                        MemoryHandle pin = _pin;
                        Return();
                        pin.Dispose();
                    }
                }
            }

            private void Complete(int result)
            {
                MemoryHandle pin = _pin;
                Action<int, Memory<byte>, SocketFlags, SocketError> callback = _callback!;
                Return();
                CompleteReceiveOrSend(pin, callback, result);
            }

            private void Return()
            {
                _pin = default;
                _callback = null;
                // User callbacks (including Unpin) may immediately submit another receive.
                Interlocked.CompareExchange(ref _context._cachedIoUringReceiveOperation, this, null);
            }
        }

        /// <summary>
        /// Attempts to complete a plain, single-buffer, no-destination-address Send via io_uring
        /// instead of registering the socket for epoll-based readiness notification. See
        /// <see cref="TryReceiveViaIoUring"/> for the submission/callback contract.
        /// </summary>
        private unsafe bool TrySendViaIoUring(Memory<byte> buffer, int offset, int count, SocketFlags flags, Action<int, Memory<byte>, SocketFlags, SocketError> callback)
        {
            if (!System.Threading.IoUring.IsSupported || flags != SocketFlags.None)
            {
                return false;
            }

            MemoryHandle pin = buffer.Pin();
            byte* bufferPtr = (byte*)pin.Pointer + offset;
            bool submitted = System.Threading.IoUring.TrySubmitSend(
                _socket,
                bufferPtr,
                count,
                0,
                result => CompleteReceiveOrSend(pin, callback, result));

            if (!submitted)
            {
                pin.Dispose();
            }

            return submitted;
        }

        private static void CompleteReceiveOrSend(MemoryHandle pin, Action<int, Memory<byte>, SocketFlags, SocketError> callback, int result)
        {
            pin.Dispose();

            int bytesTransferred = result >= 0 ? result : 0;
            SocketError errorCode = result >= 0
                ? SocketError.Success
                : SocketPal.GetSocketErrorForErrorCode(new Interop.ErrorInfo(-result).Error);

            callback(bytesTransferred, Memory<byte>.Empty, SocketFlags.None, errorCode);
        }

        /// <summary>
        /// Attempts to complete an Accept via io_uring instead of registering the listening socket for
        /// epoll-based readiness notification. <paramref name="socketAddress"/> must remain valid until
        /// <paramref name="callback"/> is invoked (it receives the peer's address, sliced to its actual
        /// length, on success). See <see cref="TryReceiveViaIoUring"/> for the general
        /// submission/callback contract.
        /// </summary>
        private unsafe bool TryAcceptViaIoUring(Memory<byte> socketAddress, Action<IntPtr, Memory<byte>, SocketError> callback)
        {
            if (!System.Threading.IoUring.IsSupported)
            {
                return false;
            }

            MemoryHandle addressPin = socketAddress.Pin();

            // The io_uring Accept op needs a pinned, in/out socklen_t for the address length: the
            // kernel writes the actual peer address length back into it on completion. A GC-pinned
            // array (rather than a stack-allocated int, which wouldn't survive past this synchronous
            // call) keeps this alive and at a stable address for as long as the operation is in
            // flight, without requiring an explicit GCHandle to free later.
            int[] addressLengthBox = GC.AllocateArray<int>(1, pinned: true);
            addressLengthBox[0] = socketAddress.Length;

            bool submitted;
            fixed (int* addressLengthPtr = addressLengthBox)
            {
                submitted = System.Threading.IoUring.TrySubmitAccept(
                    _socket,
                    (byte*)addressPin.Pointer,
                    addressLengthPtr,
                    0,
                    result =>
                    {
                        addressPin.Dispose();

                        if (result >= 0)
                        {
                            int actualLength = Math.Min(addressLengthBox[0], socketAddress.Length);
                            callback((IntPtr)result, socketAddress.Slice(0, actualLength), SocketError.Success);
                        }
                        else
                        {
                            SocketError errorCode = SocketPal.GetSocketErrorForErrorCode(new Interop.ErrorInfo(-result).Error);
                            callback((IntPtr)(-1), socketAddress, errorCode);
                        }
                    });
            }

            if (!submitted)
            {
                addressPin.Dispose();
            }

            return submitted;
        }

        /// <summary>
        /// Attempts to complete a Connect (with no data to send alongside it - TCP Fast Open-style
        /// connect-with-data always falls back to the existing path) via io_uring instead of the
        /// existing non-blocking-connect-then-epoll-wait sequence. See
        /// <see cref="TryReceiveViaIoUring"/> for the general submission/callback contract.
        /// </summary>
        private unsafe bool TryConnectViaIoUring(Memory<byte> socketAddress, Action<int, Memory<byte>, SocketFlags, SocketError> callback)
        {
            if (!System.Threading.IoUring.IsSupported)
            {
                return false;
            }

            MemoryHandle addressPin = socketAddress.Pin();
            int[] addressLengthBox = GC.AllocateArray<int>(1, pinned: true);
            addressLengthBox[0] = socketAddress.Length;

            bool submitted;
            fixed (int* addressLengthPtr = addressLengthBox)
            {
                submitted = System.Threading.IoUring.TrySubmitConnect(
                    _socket,
                    (byte*)addressPin.Pointer,
                    addressLengthPtr,
                    result =>
                    {
                        addressPin.Dispose();

                        SocketError errorCode = result == 0
                            ? SocketError.Success
                            : SocketPal.GetSocketErrorForErrorCode(new Interop.ErrorInfo(-result).Error);

                        _socket.RegisterConnectResult(errorCode);
                        _socket.SetBlocking();

                        callback(0, socketAddress, SocketFlags.None, errorCode);
                    });
            }

            if (!submitted)
            {
                addressPin.Dispose();
            }

            return submitted;
        }
    }
}
