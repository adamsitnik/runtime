// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace System.Threading
{
    internal sealed partial class PortableThreadPool
    {
        internal static partial class IoUringThreadPool
        {
            /// <summary>
            /// Ring-owned pool of provided buffers (buf_group 0) used by every
            /// <see cref="Interop.Sys.IoRingOp.RecvMultishot"/> request submitted to this ring - see
            /// <see cref="Interop.Sys.IoRingRegisterBufferRing"/>. Backed by natively-allocated,
            /// page-aligned storage that the ring itself owns and frees on close. A completion selects a
            /// buffer by id (0..<see cref="BufferCount"/>-1); <see cref="Rent"/> wraps that buffer's
            /// slice as a distinct <see cref="IMemoryOwner{Byte}"/> without copying. The owner must not
            /// be reused: a repeated Dispose on an old owner must never return a subsequent lease.
            /// </summary>
            internal sealed unsafe class ReceiveBufferPool
            {
                private const int BitsPerReturnWord = 64;

                public readonly int BufferSize;
                public readonly int BufferCount;

                private readonly Ring _ring;
                private readonly byte* _storage;
                private readonly long[] _returnedBuffers;
                private IoUringOperation? _waitingReceives;
                private IoUringOperation? _lastWaitingReceive;

                // Issuer-owned count: selected buffers leave at CQE reaping, and returns
                // reenter only after being published to the kernel.
                internal int AvailableBuffers;

                internal ReceiveBufferPool(Ring ring, int bufferSize, int bufferCount, byte* storage)
                {
                    _ring = ring;
                    BufferSize = bufferSize;
                    BufferCount = bufferCount;
                    AvailableBuffers = bufferCount;
                    _storage = storage;
                    _returnedBuffers = new long[checked((bufferCount + BitsPerReturnWord - 1) / BitsPerReturnWord)];
                }

                public IMemoryOwner<byte> Rent(int bufferId, int length)
                {
                    Debug.Assert((uint)bufferId < (uint)BufferCount && (uint)length <= (uint)BufferSize);
                    return new ReceiveBufferLease(this, bufferId, _storage + (long)bufferId * BufferSize, length);
                }

                /// <summary>
                /// Queues <paramref name="bufferId"/> to be republished to the kernel - see
                /// <see cref="DrainReturns"/>. Each id has at most one outstanding return, so a
                /// bit per buffer tracks returns without preserving their order.
                /// May be called from any thread (whichever one disposes the corresponding
                /// <see cref="ReceiveBufferLease"/>).
                /// </summary>
                internal void Return(int bufferId)
                {
                    Interlocked.Or(ref _returnedBuffers[bufferId / BitsPerReturnWord], 1L << (bufferId % BitsPerReturnWord));
                    if (Volatile.Read(ref _waitingReceives) is not null)
                    {
                        WakeIssuer(_ring);
                    }
                }

                internal bool HasReadyWaiters => _waitingReceives is not null && AvailableBuffers > 0;

                internal bool HasPendingReturns
                {
                    get
                    {
                        if (_waitingReceives is not null)
                        {
                            foreach (ref long word in _returnedBuffers.AsSpan())
                            {
                                if (Volatile.Read(ref word) != 0)
                                {
                                    return true;
                                }
                            }
                        }
                        return false;
                    }
                }

                internal void AddWaiter(IoUringOperation operation, in Interop.Sys.IoRingRequest request)
                {
                    Debug.Assert(AvailableBuffers == 0);
                    operation._waitingRequest = request;
                    operation._previousWaiter = _lastWaitingReceive;
                    if (_lastWaitingReceive is null)
                    {
                        Volatile.Write(ref _waitingReceives, operation);
                    }
                    else
                    {
                        _lastWaitingReceive._nextWaiter = operation;
                    }
                    _lastWaitingReceive = operation;
                    LinkOperation(operation, published: false);
                }

                internal void RemoveWaiter(IoUringOperation operation)
                {
                    if (operation._previousWaiter is null)
                    {
                        Debug.Assert(_waitingReceives == operation);
                        Volatile.Write(ref _waitingReceives, operation._nextWaiter);
                    }
                    else
                    {
                        operation._previousWaiter._nextWaiter = operation._nextWaiter;
                    }
                    if (operation._nextWaiter is null)
                    {
                        _lastWaitingReceive = operation._previousWaiter;
                    }
                    else
                    {
                        operation._nextWaiter._previousWaiter = operation._previousWaiter;
                    }
                    operation._previousWaiter = null;
                    operation._nextWaiter = null;
                    operation._waitingRequest = default;
                    UnlinkOperation(operation);
                }

                internal bool TryResume(out Interop.Sys.IoRingRequest request)
                {
                    if (HasReadyWaiters)
                    {
                        IoUringOperation operation = _waitingReceives!;
                        request = operation._waitingRequest;
                        RemoveWaiter(operation);
                        return true;
                    }
                    request = default;
                    return false;
                }

                internal void DrainReturns(ushort[] batch)
                {
                    int count = 0;
                    for (int word = 0; word < _returnedBuffers.Length; word++)
                    {
                        if (Volatile.Read(ref _returnedBuffers[word]) == 0)
                        {
                            continue;
                        }

                        ulong returned = (ulong)Interlocked.Exchange(ref _returnedBuffers[word], 0);
                        while (returned != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(returned);
                            returned &= returned - 1;
                            batch[count++] = (ushort)(word * BitsPerReturnWord + bit);
                            if (count == batch.Length)
                            {
                                PublishReturns(batch, count);
                                count = 0;
                            }
                        }
                    }

                    if (count != 0)
                    {
                        PublishReturns(batch, count);
                    }
                }

                private void PublishReturns(ushort[] batch, int count)
                {
                    fixed (ushort* batchPtr = batch)
                    {
                        if (Interop.Sys.IoRingReturnBuffers(_ring.RingHandle, batchPtr, count) != 0)
                        {
                            Environment.FailFast($"io_uring provided-buffer return failed: {Marshal.GetLastPInvokeError()}.");
                        }
                    }
                    AvailableBuffers += count;
                    Debug.Assert(AvailableBuffers <= BufferCount);
                }
            }

            /// <summary>
            /// A single-use <see cref="IMemoryOwner{Byte}"/> over one fixed provided buffer
            /// (see <see cref="ReceiveBufferPool"/>). <see cref="Dispose"/> returns the buffer to its
            /// pool instead of freeing anything - the backing storage is owned by the ring itself.
            /// </summary>
            private sealed unsafe class ReceiveBufferLease : MemoryManager<byte>
            {
                private ReceiveBufferPool? _pool;
                private readonly int _bufferId;
                private readonly byte* _basePointer;
                private readonly int _length;

                public ReceiveBufferLease(ReceiveBufferPool pool, int bufferId, byte* basePointer, int length)
                {
                    _pool = pool;
                    _bufferId = bufferId;
                    _basePointer = basePointer;
                    _length = length;
                }

                public override Span<byte> GetSpan()
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _pool) is null, this);
                    return new Span<byte>(_basePointer, _length);
                }

                public override MemoryHandle Pin(int elementIndex = 0)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _pool) is null, this);
                    if ((uint)elementIndex > (uint)_length)
                    {
                        throw new ArgumentOutOfRangeException(nameof(elementIndex));
                    }

                    return new MemoryHandle(_basePointer + elementIndex);
                }

                public override void Unpin()
                {
                }

                protected override void Dispose(bool disposing) => Interlocked.Exchange(ref _pool, null)?.Return(_bufferId);
            }

        }
    }
}
