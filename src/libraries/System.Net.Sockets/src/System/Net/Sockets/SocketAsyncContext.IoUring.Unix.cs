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
    // Each ring has one issuer; callbacks run on Thread Pool workers. Operations without a
    // completion-based adapter use the ordered queues with io_uring readiness notifications.
    internal sealed partial class SocketAsyncContext
    {
        private IoRingBoundHandle? _ioUringBinding;
        private bool _ioUringReadinessRegistered;
        private IoUringBufferOperation? _cachedIoUringReceiveOperation;
        private IoUringBufferOperation? _cachedIoUringSendOperation;
        private IoUringBufferListSendOperation? _cachedIoUringBufferListSendOperation;
        private IoUringAddressOperation? _cachedIoUringAcceptOperation;
        private IoUringAddressOperation? _cachedIoUringConnectOperation;

        private sealed class IoUringPollOperation : IoUringOperation
        {
            private readonly SocketAsyncContext _context;
            private readonly bool _isRead;
            private int _active;

            public IoUringPollOperation(SocketAsyncContext context, bool isRead)
            {
                _context = context;
                _isRead = isRead;
            }

            protected override unsafe IoUringRequest Request =>
                new IoUringRequest(_isRead ? IoUringOperationKind.PollRead : IoUringOperationKind.PollWrite, null, 0);

            public void Start()
            {
                if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
                {
                    return;
                }

                try
                {
                    _context.IoUringBinding.Enqueue(this);
                }
                catch (ObjectDisposedException)
                {
                    Notify(SocketError.OperationAborted);
                }
                catch (OutOfMemoryException)
                {
                    Notify(SocketError.NoBufferSpaceAvailable);
                }
            }

            protected override void OnCompleted(int result, uint flags, long sequence)
            {
                CompleteOperation();
                Notify(result >= 0 ? SocketError.Success : SocketPal.GetSocketErrorForErrorCode(new Interop.ErrorInfo(-result).Error));
            }

            private void Notify(SocketError error)
            {
                Volatile.Write(ref _active, 0);
                if (_isRead)
                {
                    _context._receiveQueue.HandleIoUringReadiness(_context, error);
                }
                else
                {
                    _context._sendQueue.HandleIoUringReadiness(_context, error);
                }
            }
        }

        private IoRingBoundHandle IoUringBinding
        {
            get
            {
                IoRingBoundHandle? binding = Volatile.Read(ref _ioUringBinding);
                if (binding is null)
                {
                    binding = IoUring.Bind(_socket);
                    binding = Interlocked.CompareExchange(ref _ioUringBinding, binding, null) ?? binding;
                }
                return binding;
            }
        }

        /// <summary>
        /// Attempts to complete a plain, single-buffer, no-destination-address Receive via io_uring
        /// instead of registering the socket for epoll-based readiness notification. Returns
        /// <see langword="true"/> if the operation was submitted - <paramref name="callback"/> will be
        /// invoked exactly once, later, with the final result (bytes received, or a mapped
        /// <see cref="SocketError"/> on failure). Returns <see langword="false"/> if the fast path does
        /// not apply; the caller must fall back to its normal code path and no callback will be invoked
        /// for this attempt. Submission exceptions are propagated before accepting the operation.
        /// </summary>
        private bool TryReceiveViaIoUring(Memory<byte> buffer, SocketFlags flags, Action<int, Memory<byte>, SocketFlags, SocketError> callback,
            CancellationToken cancellationToken)
        {
            if (!System.Threading.IoUring.IsSupported || !_socket.IsSocket || flags != SocketFlags.None || buffer.Length == 0)
            {
                return false;
            }

            IoUringBufferOperation operation = Interlocked.Exchange(ref _cachedIoUringReceiveOperation, null)
                ?? new IoUringBufferOperation(this, isReceive: true);
            if (!_receiveQueue.TryStartNativeOperation())
            {
                Interlocked.CompareExchange(ref _cachedIoUringReceiveOperation, operation, null);
                return false;
            }
            return operation.TrySubmit(buffer, 0, buffer.Length, 0, callback, cancellationToken);
        }

        private sealed class IoUringBufferOperation : IoUringOperation
        {
            private readonly SocketAsyncContext _context;
            private readonly bool _isReceive;
            private MemoryHandle _pin;
            private int _offset;
            private int _count;
            private int _bytesAlreadyTransferred;
            private Action<int, Memory<byte>, SocketFlags, SocketError>? _callback;

            public IoUringBufferOperation(SocketAsyncContext context, bool isReceive)
            {
                _context = context;
                _isReceive = isReceive;
            }

            protected override unsafe IoUringRequest Request =>
                new IoUringRequest(_isReceive ? IoUringOperationKind.Receive : IoUringOperationKind.Send,
                    (byte*)_pin.Pointer + _offset, _count);

            public unsafe bool TrySubmit(Memory<byte> buffer, int offset, int count, int bytesAlreadyTransferred,
                Action<int, Memory<byte>, SocketFlags, SocketError> callback, CancellationToken cancellationToken)
            {
                bool submitted = false;
                try
                {
                    _pin = buffer.Pin();
                    _callback = callback;
                    _bytesAlreadyTransferred = bytesAlreadyTransferred;
                    _offset = offset;
                    _count = count;
                    _context.IoUringBinding.Enqueue(this, cancellationToken);
                    submitted = true;
                    return true;
                }
                finally
                {
                    if (!submitted)
                    {
                        MemoryHandle pin = _pin;
                        Return();
                        CompleteQueue();
                        pin.Dispose();
                    }
                }
            }

            protected override void OnCompleted(int result, uint flags, long sequence)
            {
                SocketError error = result < 0
                    ? SocketPal.GetSocketErrorForErrorCode(new Interop.ErrorInfo(-result).Error)
                    : SocketError.Success;
                if (result >= 0)
                {
                    _bytesAlreadyTransferred += result;
                    if (!_isReceive)
                    {
                        _offset += result;
                        _count -= result;
                        if (_count != 0)
                        {
                            if (IsCancellationRequested)
                            {
                                error = SocketError.OperationAborted;
                            }
                            else if (result == 0)
                            {
                                error = SocketError.ConnectionReset;
                            }
                            else
                            {
                                try
                                {
                                    EnqueueContinuation(Request);
                                    return;
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
                }

                MemoryHandle pin = _pin;
                Action<int, Memory<byte>, SocketFlags, SocketError> callback = _callback!;
                int bytesAlreadyTransferred = _bytesAlreadyTransferred;
                CompleteOperation();
                Return();
                CompleteQueue();
                pin.Dispose();
                callback(bytesAlreadyTransferred, Memory<byte>.Empty, SocketFlags.None, error);
            }

            private void CompleteQueue()
            {
                if (_isReceive)
                {
                    _context._receiveQueue.CompleteNativeOperation();
                }
                else
                {
                    _context._sendQueue.CompleteNativeOperation();
                }
            }

            private void Return()
            {
                _pin = default;
                _callback = null;
                // User callbacks (including Unpin) may immediately submit another receive.
                if (_isReceive)
                {
                    Interlocked.CompareExchange(ref _context._cachedIoUringReceiveOperation, this, null);
                }
                else
                {
                    Interlocked.CompareExchange(ref _context._cachedIoUringSendOperation, this, null);
                }
            }
        }

        /// <summary>
        /// Attempts to complete a plain, single-buffer, no-destination-address Send via io_uring
        /// instead of registering the socket for epoll-based readiness notification. See
        /// <see cref="TryReceiveViaIoUring"/> for the submission/callback contract.
        /// </summary>
        private bool TrySendViaIoUring(Memory<byte> buffer, int offset, int count, SocketFlags flags, int bytesSent,
            Action<int, Memory<byte>, SocketFlags, SocketError> callback, CancellationToken cancellationToken)
        {
            if (!System.Threading.IoUring.IsSupported || !_socket.IsSocket || flags != SocketFlags.None)
            {
                return false;
            }

            IoUringBufferOperation operation = Interlocked.Exchange(ref _cachedIoUringSendOperation, null)
                ?? new IoUringBufferOperation(this, isReceive: false);
            if (!_sendQueue.TryStartNativeOperation())
            {
                Interlocked.CompareExchange(ref _cachedIoUringSendOperation, operation, null);
                return false;
            }
            return operation.TrySubmit(buffer, offset, count, bytesSent, callback, cancellationToken);
        }

        private bool TrySendViaIoUring(IList<ArraySegment<byte>> buffers, int bufferIndex, int offset, SocketFlags flags,
            int bytesSent, Action<int, Memory<byte>, SocketFlags, SocketError> callback)
        {
            if (!System.Threading.IoUring.IsSupported || !_socket.IsSocket || flags != SocketFlags.None)
            {
                return false;
            }

            IoUringBufferListSendOperation operation = Interlocked.Exchange(ref _cachedIoUringBufferListSendOperation, null)
                ?? new IoUringBufferListSendOperation(this);
            if (!_sendQueue.TryStartNativeOperation())
            {
                Interlocked.CompareExchange(ref _cachedIoUringBufferListSendOperation, operation, null);
                return false;
            }
            return operation.TrySubmit(buffers, bufferIndex, offset, bytesSent, callback);
        }

        private sealed class IoUringBufferListSendOperation : IoUringOperation
        {
            private readonly SocketAsyncContext _context;
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
                    _context.IoUringBinding.Enqueue(this);
                    submitted = true;
                    return true;
                }
                finally
                {
                    if (!submitted)
                    {
                        Return();
                        _context._sendQueue.CompleteNativeOperation();
                    }
                }
            }

            protected override unsafe IoUringRequest Request =>
                new IoUringRequest(IoUringOperationKind.SendGather,
                    (Interop.Sys.IOVector*)_vectorsPin.AddrOfPinnedObject() + _vectorIndex, _pinCount - _vectorIndex);

            protected override unsafe void OnCompleted(int result, uint flags, long sequence)
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
                        if (IsCancellationRequested)
                        {
                            error = SocketError.OperationAborted;
                        }
                        else if (result == 0)
                        {
                            error = SocketError.ConnectionReset;
                        }
                        else
                        {
                            try
                            {
                                EnqueueContinuation(Request);
                                return;
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
                CompleteOperation();
                Return();
                _context._sendQueue.CompleteNativeOperation();
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

        /// <summary>
        /// Attempts to complete an Accept via io_uring instead of registering the listening socket for
        /// epoll-based readiness notification. <paramref name="socketAddress"/> must remain valid until
        /// <paramref name="callback"/> is invoked (it receives the peer's address, sliced to its actual
        /// length, on success). See <see cref="TryReceiveViaIoUring"/> for the general
        /// submission/callback contract.
        /// </summary>
        private bool TryAcceptViaIoUring(Memory<byte> socketAddress, Action<IntPtr, Memory<byte>, SocketError> callback,
            CancellationToken cancellationToken)
        {
            if (!System.Threading.IoUring.IsSupported)
            {
                return false;
            }

            IoUringAddressOperation operation = Interlocked.Exchange(ref _cachedIoUringAcceptOperation, null)
                ?? new IoUringAddressOperation(this, isAccept: true);
            if (!_receiveQueue.TryStartNativeOperation())
            {
                Interlocked.CompareExchange(ref _cachedIoUringAcceptOperation, operation, null);
                return false;
            }
            return operation.TrySubmit(socketAddress, callback, null, cancellationToken);
        }

        /// <summary>
        /// Attempts to complete a Connect (with no data to send alongside it - TCP Fast Open-style
        /// connect-with-data always falls back to the existing path) via io_uring instead of the
        /// existing non-blocking-connect-then-readiness-wait sequence. See
        /// <see cref="TryReceiveViaIoUring"/> for the general submission/callback contract.
        /// </summary>
        private bool TryConnectViaIoUring(Memory<byte> socketAddress, Action<int, Memory<byte>, SocketFlags, SocketError> callback,
            CancellationToken cancellationToken)
        {
            if (!System.Threading.IoUring.IsSupported)
            {
                return false;
            }

            IoUringAddressOperation operation = Interlocked.Exchange(ref _cachedIoUringConnectOperation, null)
                ?? new IoUringAddressOperation(this, isAccept: false);
            if (!_sendQueue.TryStartNativeOperation())
            {
                Interlocked.CompareExchange(ref _cachedIoUringConnectOperation, operation, null);
                return false;
            }
            return operation.TrySubmit(socketAddress, null, callback, cancellationToken);
        }

        private sealed class IoUringAddressOperation : IoUringOperation
        {
            private readonly SocketAsyncContext _context;
            private readonly bool _isAccept;
            private readonly int[] _addressLength = GC.AllocateArray<int>(1, pinned: true);
            private Memory<byte> _address;
            private MemoryHandle _pin;
            private Action<IntPtr, Memory<byte>, SocketError>? _acceptCallback;
            private Action<int, Memory<byte>, SocketFlags, SocketError>? _connectCallback;

            public IoUringAddressOperation(SocketAsyncContext context, bool isAccept)
            {
                _context = context;
                _isAccept = isAccept;
            }

            protected override unsafe IoUringRequest Request
            {
                get
                {
                    fixed (int* addressLength = _addressLength)
                    {
                        return new IoUringRequest(_isAccept ? IoUringOperationKind.Accept : IoUringOperationKind.Connect,
                            null, 0, socketAddress: _pin.Pointer, socketAddressLength: addressLength);
                    }
                }
            }

            public bool TrySubmit(Memory<byte> address, Action<IntPtr, Memory<byte>, SocketError>? acceptCallback,
                Action<int, Memory<byte>, SocketFlags, SocketError>? connectCallback, CancellationToken cancellationToken)
            {
                bool submitted = false;
                try
                {
                    _address = address;
                    _addressLength[0] = address.Length;
                    _acceptCallback = acceptCallback;
                    _connectCallback = connectCallback;
                    _pin = address.Pin();
                    _context.IoUringBinding.Enqueue(this, cancellationToken);
                    submitted = true;
                    return true;
                }
                finally
                {
                    if (!submitted)
                    {
                        MemoryHandle pin = _pin;
                        Return();
                        pin.Dispose();
                        CompleteQueue();
                    }
                }
            }

            protected override void OnCompleted(int result, uint flags, long sequence)
            {
                MemoryHandle pin = _pin;
                Memory<byte> address = _address;
                int addressLength = Math.Min(_addressLength[0], address.Length);
                Action<IntPtr, Memory<byte>, SocketError>? acceptCallback = _acceptCallback;
                Action<int, Memory<byte>, SocketFlags, SocketError>? connectCallback = _connectCallback;
                SocketError error = result >= 0
                    ? SocketError.Success
                    : SocketPal.GetSocketErrorForErrorCode(new Interop.ErrorInfo(-result).Error);
                CompleteOperation();
                Return();
                pin.Dispose();

                if (_isAccept)
                {
                    CompleteQueue();
                    acceptCallback!((IntPtr)(result >= 0 ? result : -1),
                        result >= 0 ? address.Slice(0, addressLength) : address, error);
                }
                else
                {
                    _context._socket.RegisterConnectResult(error);
                    CompleteQueue();
                    connectCallback!(0, address, SocketFlags.None, error);
                }
            }

            private void CompleteQueue()
            {
                if (_isAccept)
                {
                    _context._receiveQueue.CompleteNativeOperation();
                }
                else
                {
                    _context._sendQueue.CompleteNativeOperation();
                }
            }

            private void Return()
            {
                _pin = default;
                _address = default;
                _acceptCallback = null;
                _connectCallback = null;
                if (_isAccept)
                {
                    Interlocked.CompareExchange(ref _context._cachedIoUringAcceptOperation, this, null);
                }
                else
                {
                    Interlocked.CompareExchange(ref _context._cachedIoUringConnectOperation, this, null);
                }
            }
        }
    }
}
