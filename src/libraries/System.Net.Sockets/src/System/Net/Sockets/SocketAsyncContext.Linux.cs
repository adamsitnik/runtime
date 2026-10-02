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
        private bool _ioUringReadinessRegistered;
        private IoUringBufferOperation? _bufferOperation;

        // Each socket has separate receive and send queues, shared by synchronous calls,
        // syscall-based async adapters and direct io_uring adapters. Direct kernel I/O must not
        // bypass earlier work or let a later syscall consume its bytes. TryReserveForIoUringOperation
        // therefore changes an empty Ready queue to Processing before pinning/enqueueing. The
        // reservation represents ordering, not SQ publication; failure cleanup must release it too.
        // Later operations join the normal queue. CompleteIoUringOperation releases the reservation
        // and dispatches the next operation outside the queue lock, or returns the queue to Ready.
        //
        // Not every operation has a direct adapter (for example, addressed/flagged receives).
        // Those still attempt a nonblocking syscall and enter Waiting on EAGAIN. In io_uring mode,
        // EnsureIoUringReadiness arms a one-shot POLL_ADD instead of registering with epoll.
        // HandleIoUringReadiness feeds its result into the existing queue processing path, which
        // retries the syscall; a readiness completion is not a data-transfer completion.
        // Synchronous waiters poll themselves, identified by IsWaitingSynchronously, rather than
        // depending on a ThreadPool callback to unblock. Arming and dispatch run outside the lock
        // because submission failures may notify the queue immediately and ultimately run user code.
        // Queue fields stay with the Unix declaration to keep this struct's layout in one partial.
        private partial struct OperationQueue<TOperation>
            where TOperation : AsyncOperation
        {
            // Reserve before pinning/enqueueing a direct request. Success does not mean SQ submission.
            public bool TryReserveForIoUringOperation()
            {
                using (Lock())
                {
                    if (_state != QueueState.Ready)
                    {
                        return false;
                    }

                    Debug.Assert(_tail is null && !_ioUringOperationPending);
                    _ioUringOperationPending = true;
                    _state = QueueState.Processing;
                    return true;
                }
            }

            // Release the direct request's queue position and resume any ordered follower.
            public void CompleteIoUringOperation()
            {
                AsyncOperation? next = null;
                using (Lock())
                {
                    Debug.Assert(_ioUringOperationPending);
                    _ioUringOperationPending = false;
                    if (_state != QueueState.Stopped)
                    {
                        Debug.Assert(_state == QueueState.Processing);
                        if (_tail is null)
                        {
                            _state = QueueState.Ready;
                            _sequenceNumber++;
                        }
                        else
                        {
                            next = _tail.Next;
                        }
                    }
                }
                next?.Dispatch();
            }

            // A syscall-based adapter that would block needs readability/writability notification,
            // not a direct I/O completion. Arm one poll outside the lock; synchronous callers poll themselves.
            private void EnsureIoUringReadiness()
            {
                if (!IoUring.IsSupported)
                {
                    return;
                }

                using (Lock())
                {
                    if (_state != QueueState.Waiting || _isNextOperationSynchronous)
                    {
                        return;
                    }
                }
                _readinessOperation!.Start();
            }

            public bool IsWaitingSynchronously(TOperation operation)
            {
                using (Lock())
                {
                    return _state == QueueState.Waiting && _tail?.Next == operation;
                }
            }

            public void HandleIoUringReadiness(SocketAsyncContext context, SocketError error)
            {
                using (Lock())
                {
                    if (_state == QueueState.Stopped)
                    {
                        return;
                    }
                    if (error != SocketError.Success)
                    {
                        _readinessError = error;
                    }
                }
                context.HandleEvents(typeof(TOperation) == typeof(ReadOperation)
                    ? Interop.Sys.SocketEvents.Read : Interop.Sys.SocketEvents.Write);
            }
        }

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
                    _context.IoUringBinding.EnqueueForSubmission(this);
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

        private IoRingBoundHandle IoUringBinding => _socket.IoUringBinding;

        /// <summary>
        /// Attempts to complete a plain, single-buffer, no-destination-address Receive via io_uring
        /// instead of registering the socket for epoll-based readiness notification. Returns
        /// <see langword="true"/> if the operation was queued for submission - <paramref name="callback"/> will be
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

            IoUringBufferOperation operation = Interlocked.Exchange(ref _bufferOperation, null)
                ?? new IoUringBufferOperation(this);
            if (!_receiveQueue.TryReserveForIoUringOperation())
            {
                Interlocked.CompareExchange(ref _bufferOperation, operation, null);
                return false;
            }
            operation.EnqueueForSubmission(buffer, 0, buffer.Length, 0, callback, cancellationToken, isReceive: true);
            return true;
        }

        private sealed class IoUringBufferOperation : IoUringOperation
        {
            private readonly SocketAsyncContext _context;
            private bool _isReceive;
            private MemoryHandle _pin;
            private int _offset;
            private int _count;
            private int _bytesAlreadyTransferred;
            private Action<int, Memory<byte>, SocketFlags, SocketError>? _callback;

            public IoUringBufferOperation(SocketAsyncContext context)
            {
                _context = context;
            }

            protected override unsafe IoUringRequest Request =>
                new IoUringRequest(_isReceive ? IoUringOperationKind.Receive : IoUringOperationKind.Send,
                    (byte*)_pin.Pointer + _offset, _count);

            public unsafe void EnqueueForSubmission(Memory<byte> buffer, int offset, int count, int bytesAlreadyTransferred,
                Action<int, Memory<byte>, SocketFlags, SocketError> callback, CancellationToken cancellationToken, bool isReceive)
            {
                _isReceive = isReceive;
                _callback = callback;
                _bytesAlreadyTransferred = bytesAlreadyTransferred;
                _offset = offset;
                _count = count;
                try
                {
                    // Pin can invoke a user MemoryManager and throw; the queue reservation still needs release.
                    _pin = buffer.Pin();
                    _context.IoUringBinding.EnqueueForSubmission(this, cancellationToken);
                }
                catch
                {
                    MemoryHandle pin = _pin;
                    Return();
                    CompleteQueue(isReceive);
                    pin.Dispose();
                    throw;
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
                bool isReceive = _isReceive;
                CompleteOperation();
                Return();
                CompleteQueue(isReceive);
                pin.Dispose();
                callback(bytesAlreadyTransferred, Memory<byte>.Empty, SocketFlags.None, error);
            }

            private void CompleteQueue(bool isReceive)
            {
                if (isReceive)
                {
                    _context._receiveQueue.CompleteIoUringOperation();
                }
                else
                {
                    _context._sendQueue.CompleteIoUringOperation();
                }
            }

            private void Return()
            {
                _pin = default;
                _callback = null;
                // User callbacks (including Unpin) may immediately submit another operation.
                Interlocked.CompareExchange(ref _context._bufferOperation, this, null);
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

            IoUringBufferOperation operation = Interlocked.Exchange(ref _bufferOperation, null)
                ?? new IoUringBufferOperation(this);
            if (!_sendQueue.TryReserveForIoUringOperation())
            {
                Interlocked.CompareExchange(ref _bufferOperation, operation, null);
                return false;
            }
            operation.EnqueueForSubmission(buffer, offset, count, bytesSent, callback, cancellationToken, isReceive: false);
            return true;
        }

        private bool TrySendViaIoUring(IList<ArraySegment<byte>> buffers, int bufferIndex, int offset, SocketFlags flags,
            int bytesSent, Action<int, Memory<byte>, SocketFlags, SocketError> callback)
        {
            if (!System.Threading.IoUring.IsSupported || !_socket.IsSocket || flags != SocketFlags.None)
            {
                return false;
            }

            IoUringBufferListSendOperation operation = new(this);
            if (!_sendQueue.TryReserveForIoUringOperation())
            {
                return false;
            }
            operation.EnqueueForSubmission(buffers, bufferIndex, offset, bytesSent, callback);
            return true;
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

            public unsafe void EnqueueForSubmission(IList<ArraySegment<byte>> buffers, int bufferIndex, int offset, int bytesSent,
                Action<int, Memory<byte>, SocketFlags, SocketError> callback)
            {
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
                    _context.IoUringBinding.EnqueueForSubmission(this);
                }
                catch
                {
                    Return();
                    _context._sendQueue.CompleteIoUringOperation();
                    throw;
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
                    _vectorIndex += Interop.Sys.AdvanceIOVectors(_vectors.AsSpan(_vectorIndex, _pinCount - _vectorIndex), result);

                    // MSG_WAITALL covers only the submitted native batch: native SENDMSG caps a
                    // stream request at IOV_MAX, so a full batch can still leave managed vectors.
                    // A genuinely short WAITALL result is not limited to peer close either:
                    // Linux returns accumulated progress when an error follows a partial send.
                    // The positive CQE alone does not distinguish that from a successful batch.
                    // Match the epoll TryCompleteSendTo loop by sending the remainder rather than
                    // treating every positive CQE as final success. Keep pins until the logical
                    // send finishes, and report accumulated bytes alongside any eventual error.
                    // SocketAsyncEventArgs preserves BytesTransferred on failure; Task/ValueTask
                    // wrappers throw instead of returning that count, as they do for epoll.
                    // https://github.com/torvalds/linux/blob/v6.12/io_uring/net.c#L545-L571
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
                _context._sendQueue.CompleteIoUringOperation();
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

            IoUringAddressOperation operation = new(this, isAccept: true);
            if (!_receiveQueue.TryReserveForIoUringOperation())
            {
                return false;
            }
            operation.EnqueueForSubmission(socketAddress, callback, null, cancellationToken);
            return true;
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

            IoUringAddressOperation operation = new(this, isAccept: false);
            if (!_sendQueue.TryReserveForIoUringOperation())
            {
                return false;
            }
            operation.EnqueueForSubmission(socketAddress, null, callback, cancellationToken);
            return true;
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
                            _pin.Pointer, 0, addressLength: addressLength);
                    }
                }
            }

            public void EnqueueForSubmission(Memory<byte> address, Action<IntPtr, Memory<byte>, SocketError>? acceptCallback,
                Action<int, Memory<byte>, SocketFlags, SocketError>? connectCallback, CancellationToken cancellationToken)
            {
                try
                {
                    _address = address;
                    _addressLength[0] = address.Length;
                    _acceptCallback = acceptCallback;
                    _connectCallback = connectCallback;
                    _pin = address.Pin();
                    _context.IoUringBinding.EnqueueForSubmission(this, cancellationToken);
                }
                catch
                {
                    MemoryHandle pin = _pin;
                    Return();
                    CompleteQueue();
                    pin.Dispose();
                    throw;
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
                    _context._receiveQueue.CompleteIoUringOperation();
                }
                else
                {
                    _context._sendQueue.CompleteIoUringOperation();
                }
            }

            private void Return()
            {
                _pin = default;
                _address = default;
                _acceptCallback = null;
                _connectCallback = null;
            }
        }
    }
}
