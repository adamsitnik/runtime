// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
        private IoUringBufferListSendOperation? _cachedIoUringBufferListSendOperation;

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
        private unsafe bool TrySendViaIoUring(Memory<byte> buffer, int offset, int count, SocketFlags flags, int bytesSent, Action<int, Memory<byte>, SocketFlags, SocketError> callback)
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
                result => CompleteReceiveOrSend(pin, callback, result, bytesSent));

            if (!submitted)
            {
                pin.Dispose();
            }

            return submitted;
        }

        private bool TrySendViaIoUring(IList<ArraySegment<byte>> buffers, int bufferIndex, int offset, SocketFlags flags,
            int bytesSent, Action<int, Memory<byte>, SocketFlags, SocketError> callback)
        {
            if (!System.Threading.IoUring.IsSupported || flags != SocketFlags.None)
            {
                return false;
            }

            IoUringBufferListSendOperation operation = Interlocked.Exchange(ref _cachedIoUringBufferListSendOperation, null)
                ?? new IoUringBufferListSendOperation(this);
            return operation.TrySubmit(buffers, bufferIndex, offset, bytesSent, callback);
        }

        private sealed class IoUringBufferListSendOperation
        {
            private readonly SocketAsyncContext _context;
            private readonly Action<int> _onCompleted;
            private GCHandle[] _pins = Array.Empty<GCHandle>();
            private Interop.Sys.IOVector[] _vectors = Array.Empty<Interop.Sys.IOVector>();
            private GCHandle _vectorsPin;
            private int _pinCount;
            private int _vectorIndex;
            private int _bytesSent;
            private Action<int, Memory<byte>, SocketFlags, SocketError>? _callback;

            public IoUringBufferListSendOperation(SocketAsyncContext context)
            {
                _context = context;
                _onCompleted = Complete;
            }

            public unsafe bool TrySubmit(IList<ArraySegment<byte>> buffers, int bufferIndex, int offset, int bytesSent,
                Action<int, Memory<byte>, SocketFlags, SocketError> callback)
            {
                bool submitted = false;
                try
                {
                    int count = buffers.Count - bufferIndex;
                    if (_pins.Length < count)
                    {
                        _pins = new GCHandle[count];
                    }
                    if (_vectors.Length < count)
                    {
                        _vectors = new Interop.Sys.IOVector[count];
                    }

                    _bytesSent = bytesSent;
                    _callback = callback;
                    for (int i = 0; i < count; i++, offset = 0)
                    {
                        ArraySegment<byte> buffer = buffers[bufferIndex + i];
                        RangeValidationHelpers.ValidateSegment(buffer);
                        _pins[i] = GCHandle.Alloc(buffer.Array, GCHandleType.Pinned);
                        _pinCount++;
                        _vectors[i].Base = (byte*)_pins[i].AddrOfPinnedObject() + buffer.Offset + offset;
                        _vectors[i].Count = (UIntPtr)(buffer.Count - offset);
                    }

                    _vectorsPin = GCHandle.Alloc(_vectors, GCHandleType.Pinned);
                    submitted = Submit();
                    return submitted;
                }
                finally
                {
                    if (!submitted)
                    {
                        Return();
                    }
                }
            }

            private unsafe bool Submit() =>
                System.Threading.IoUring.TrySubmitSendV(_context._socket,
                    (Interop.Sys.IOVector*)_vectorsPin.AddrOfPinnedObject() + _vectorIndex, _pinCount - _vectorIndex, 0, _onCompleted);

            private unsafe void Complete(int result)
            {
                SocketError error = result < 0
                    ? SocketPal.GetSocketErrorForErrorCode(new Interop.ErrorInfo(-result).Error)
                    : SocketError.Success;
                if (result >= 0)
                {
                    _bytesSent += result;
                    int remaining = result;
                    while (_vectorIndex < _pinCount)
                    {
                        ref Interop.Sys.IOVector vector = ref _vectors[_vectorIndex];
                        if ((nuint)remaining < vector.Count)
                        {
                            vector.Base += remaining;
                            vector.Count -= (nuint)remaining;
                            remaining = 0;
                            break;
                        }
                        remaining -= (int)vector.Count;
                        _vectorIndex++;
                    }
                    Debug.Assert(remaining == 0);

                    // MSG_WAITALL does not span native vector limits, and errors can also
                    // produce a short result. Keep the pins until the logical send finishes.
                    if (_vectorIndex < _pinCount)
                    {
                        if (result == 0)
                        {
                            error = SocketError.ConnectionReset;
                        }
                        else
                        {
                            try
                            {
                                bool submitted = Submit();
                                Debug.Assert(submitted);
                                if (submitted)
                                {
                                    return;
                                }
                                error = SocketError.OperationNotSupported;
                            }
                            catch (ObjectDisposedException)
                            {
                                error = SocketError.OperationAborted;
                            }
                            catch (OutOfMemoryException)
                            {
                                error = SocketError.NoBufferSpaceAvailable;
                            }
                        }
                    }
                }

                Action<int, Memory<byte>, SocketFlags, SocketError> callback = _callback!;
                int bytesSent = _bytesSent;
                Return();
                callback(bytesSent, Memory<byte>.Empty, SocketFlags.None, error);
            }

            private void Return()
            {
                if (_vectorsPin.IsAllocated)
                {
                    _vectorsPin.Free();
                }
                for (int i = 0; i < _pinCount; i++)
                {
                    _pins[i].Free();
                }
                _pinCount = 0;
                _vectorIndex = 0;
                _callback = null;
                Interlocked.CompareExchange(ref _context._cachedIoUringBufferListSendOperation, this, null);
            }
        }

        private static void CompleteReceiveOrSend(MemoryHandle pin, Action<int, Memory<byte>, SocketFlags, SocketError> callback, int result, int bytesAlreadyTransferred = 0)
        {
            pin.Dispose();

            int bytesTransferred = bytesAlreadyTransferred + (result >= 0 ? result : 0);
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
