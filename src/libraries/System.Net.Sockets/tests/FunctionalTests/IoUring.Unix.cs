// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.RemoteExecutor;
using Xunit;

namespace System.Net.Sockets.Tests
{
    public class IoUringTests
    {
        public static bool IsSupported => RemoteExecutor.IsSupported && IoUring.IsSupported;
        public static bool IsRemoteExecutorSupported => RemoteExecutor.IsSupported;

        [StructLayout(LayoutKind.Sequential)]
        private struct SendVector
        {
            public IntPtr Base;
            public UIntPtr Count;
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void BoundOperation_CancellationAndReuse(bool useToken)
        {
            RemoteExecutor.Invoke(async tokenText =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle))
                {
                    Assert.Same(binding, IoUring.Bind(receiver.SafeHandle));
                    BoundReceiveOperation operation = new BoundReceiveOperation();
                    for (int i = 0; i < 50; i++)
                    {
                        using CancellationTokenSource cancellation = new CancellationTokenSource();
                        operation.Prepare();
                        binding.Enqueue(operation, bool.Parse(tokenText) ? cancellation.Token : default);
                        Assert.Throws<InvalidOperationException>(() => binding.Enqueue(operation));
                        if (bool.Parse(tokenText))
                        {
                            cancellation.Cancel();
                        }
                        else
                        {
                            operation.RequestCancellation();
                        }
                        Assert.Equal(-125, await operation.Completion.WaitAsync(TestSettings.PassingTestTimeout));
                        operation.RequestCancellation();

                        operation.Prepare();
                        binding.Enqueue(operation);
                        sender.Send(new byte[] { 42 });
                        Assert.Equal(1, await operation.Completion.WaitAsync(TestSettings.PassingTestTimeout));
                        Assert.Equal(42, operation.Value);
                    }
                }
            }, useToken.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(0, 8, false)]
        [InlineData(1, 8, false)]
        [InlineData(2, 8, false)]
        [InlineData(0, 1200, false)]
        [InlineData(0, 1200, true)]
        public void BoundHandle_Dispose_DrainsPendingReceives(int closeKind, int count, bool exhaustGenerations)
        {
            RemoteExecutor.Invoke(async (kindText, countText, exhaustText) =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle))
                {
                    BoundReceiveOperation[] operations = new BoundReceiveOperation[int.Parse(countText)];
                    object? freeSlots = null;
                    if (bool.Parse(exhaustText))
                    {
#pragma warning disable IL2075 // The RemoteExecutor process is untrimmed.
                        Type poolType = typeof(IoUring).Assembly.GetType("System.Threading.PortableThreadPool+IoUringThreadPool", throwOnError: true)!;
                        Array rings = (Array)poolType.GetField("s_rings",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
                        object ring = rings.GetValue(0)!;
                        Array slots = (Array)ring.GetType().GetField("OperationSlots")!.GetValue(ring)!;
                        freeSlots = ring.GetType().GetField("FreeOperationSlots")!.GetValue(ring)!;
                        Assert.True(operations.Length > slots.Length);
                        for (int i = 0; i < slots.Length; i++)
                        {
                            object slot = slots.GetValue(i)!;
                            slot.GetType().GetField("Generation")!.SetValue(slot, uint.MaxValue - 1);
                            slots.SetValue(slot, i);
                        }
#pragma warning restore IL2075
                    }
                    for (int i = 0; i < operations.Length; i++)
                    {
                        operations[i] = new BoundReceiveOperation();
                        operations[i].Prepare();
                        binding.Enqueue(operations[i]);
                    }
                    switch (int.Parse(kindText))
                    {
                        case 0:
                            binding.Dispose();
                            break;
                        case 1:
                            receiver.Dispose();
                            break;
                        case 2:
                            receiver.SafeHandle.Dispose();
                            break;
                    }
                    Assert.True(binding.DisposeAndWait());
                    foreach (BoundReceiveOperation operation in operations)
                    {
                        Assert.Equal(-125, await operation.Completion.WaitAsync(TestSettings.PassingTestTimeout));
                    }
                    if (freeSlots is not null)
                    {
#pragma warning disable IL2075 // The RemoteExecutor process is untrimmed.
                        Assert.Equal(0, (int)freeSlots.GetType().GetProperty("Count")!.GetValue(freeSlots)!);
#pragma warning restore IL2075
                    }
                    Assert.Throws<ObjectDisposedException>(() => binding.Enqueue(new BoundReceiveOperation()));
                    if (int.Parse(kindText) == 0)
                    {
                        sender.Send(new byte[] { 7 });
                        Assert.Equal(1, receiver.Receive(new byte[1]));
                    }
                }
            }, closeKind.ToString(), count.ToString(), exhaustGenerations.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void Bind_RacingSocketDispose_DoesNotRetainHandle()
        {
            RemoteExecutor.Invoke(async () =>
            {
                for (int i = 0; i < 100; i++)
                {
                    (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                    using (sender)
                    using (receiver)
                    {
                        SafeSocketHandle handle = receiver.SafeHandle;
                        IoRingBoundHandle? binding = null;
                        using Barrier barrier = new Barrier(2);
                        Task bind = Task.Run(() =>
                        {
                            barrier.SignalAndWait();
                            try
                            {
                                binding = IoUring.Bind(handle);
                            }
                            catch (ObjectDisposedException)
                            {
                            }
                        });
                        Task close = Task.Run(() =>
                        {
                            barrier.SignalAndWait();
                            receiver.Dispose();
                        });
                        await Task.WhenAll(bind, close).WaitAsync(TestSettings.PassingTestTimeout);
                        Assert.True(handle.IsClosed);
                        if (binding is not null)
                        {
                            Assert.Throws<ObjectDisposedException>(() => binding.Enqueue(new BoundReceiveOperation()));
                        }
                    }
                }
            }, CreateOptions(3)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void BoundOperation_CancellationAndReuseAcrossRings()
        {
            RemoteExecutor.Invoke(async () =>
            {
                List<(Socket Sender, Socket Receiver)> pairs = new List<(Socket, Socket)>();
                try
                {
                    HashSet<long> rings = new HashSet<long>();
                    for (int i = 0; i < 3; i++)
                    {
                        (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                        pairs.Add((sender, receiver));
                        rings.Add(receiver.SafeHandle.DangerousGetHandle().ToInt64() % 3);
                    }
                    Assert.True(rings.Count > 1);
                    BoundReceiveOperation operation = new BoundReceiveOperation();
                    for (int iteration = 0; iteration < 30; iteration++)
                    {
                        foreach ((Socket sender, Socket receiver) in pairs)
                        {
                            IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle);
                            using CancellationTokenSource cancellation = new CancellationTokenSource();
                            operation.Prepare();
                            binding.Enqueue(operation, cancellation.Token);
                            cancellation.Cancel();
                            Assert.Equal(-125, await operation.Completion.WaitAsync(TestSettings.PassingTestTimeout));
                            operation.Prepare();
                            binding.Enqueue(operation);
                            sender.Send(new byte[] { 42 });
                            Assert.Equal(1, await operation.Completion.WaitAsync(TestSettings.PassingTestTimeout));
                            Assert.Equal(42, operation.Value);
                        }
                    }
                }
                finally
                {
                    foreach ((Socket sender, Socket receiver) in pairs)
                    {
                        receiver.Dispose();
                        sender.Dispose();
                    }
                }
            }, CreateOptions(3)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void Bind_DisposalDuringValidation_ThrowsObjectDisposedException()
        {
            RemoteExecutor.Invoke(() =>
            {
                using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                ReleaseCounter counter = new ReleaseCounter();
                using CountingHandle handle = new CountingHandle(
                    socket.SafeHandle.DangerousGetHandle(), counter, disposeDuringValidation: true);
                Assert.Throws<ObjectDisposedException>(() => IoUring.Bind(handle));
                Assert.Equal(1, counter.Count);
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void BoundHandle_ReentrantHandleRelease_DoesNotWaitForItself()
        {
            RemoteExecutor.Invoke(async () =>
            {
                using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                ReleaseCounter counter = new ReleaseCounter();
                CountingHandle handle = new CountingHandle(socket.SafeHandle.DangerousGetHandle(), counter);
                IoRingBoundHandle binding = IoUring.Bind(handle);
                counter.OnRelease = () => binding.DisposeAndWait();
                await Task.Run(handle.Dispose).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(1, counter.Count);
                Assert.True(handle.IsClosed);
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void IdleBinding_DoesNotRootHandle_AndReleasesReference()
        {
            RemoteExecutor.Invoke(() =>
            {
                using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                ReleaseCounter counter = new ReleaseCounter();
                (WeakReference handle, WeakReference binding) = CreateCollectibleBinding(
                    socket.SafeHandle.DangerousGetHandle(), counter);
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    return !handle.IsAlive && !binding.IsAlive && Volatile.Read(ref counter.Count) == 1;
                }, TestSettings.PassingTestTimeout));
                GC.KeepAlive(socket);
            }, CreateOptions(1)).Dispose();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static (WeakReference Handle, WeakReference Binding) CreateCollectibleBinding(
            IntPtr descriptor, ReleaseCounter counter)
        {
            CountingHandle handle = new CountingHandle(descriptor, counter);
            IoRingBoundHandle binding = IoUring.Bind(handle);
            return (new WeakReference(handle), new WeakReference(binding));
        }

        private sealed class ReleaseCounter
        {
            public int Count;
            public Action? OnRelease;
        }

        private sealed class CountingHandle : SafeHandle
        {
            private readonly ReleaseCounter _counter;
            private bool _disposeDuringValidation;

            public CountingHandle(IntPtr descriptor, ReleaseCounter counter, bool disposeDuringValidation = false)
                : base(new IntPtr(-1), ownsHandle: true)
            {
                _counter = counter;
                _disposeDuringValidation = disposeDuringValidation;
                SetHandle(descriptor);
            }

            public override bool IsInvalid
            {
                get
                {
                    if (_disposeDuringValidation)
                    {
                        _disposeDuringValidation = false;
                        Dispose();
                    }
                    return IsClosed || handle == new IntPtr(-1);
                }
            }

            protected override bool ReleaseHandle()
            {
                // The socket owns the descriptor; this wrapper tracks its own lifetime only.
                Interlocked.Increment(ref _counter.Count);
                _counter.OnRelease?.Invoke();
                return true;
            }
        }

        [ConditionalFact(nameof(IsSupported))]
        public void BoundHandle_CloseOnOnlyWorker_DoesNotWaitForCallbacks()
        {
            RemoteExecutor.Invoke(async () =>
            {
                ThreadPool.GetMinThreads(out _, out int minIo);
                ThreadPool.GetMaxThreads(out _, out int maxIo);
                Assert.True(ThreadPool.SetMinThreads(1, minIo));
                Assert.True(ThreadPool.SetMaxThreads(1, maxIo));
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle))
                {
                    BoundReceiveOperation operation = new BoundReceiveOperation();
                    operation.Prepare();
                    await Task.Run(() =>
                    {
                        binding.Enqueue(operation);
                        receiver.Dispose();
                    }).WaitAsync(TestSettings.PassingTestTimeout);
                    Assert.Equal(-125, await operation.Completion.WaitAsync(TestSettings.PassingTestTimeout));
                }
            }, CreateOptions(1)).Dispose();
        }

        private sealed class BoundReceiveOperation : IoUringOperation
        {
            private readonly byte[] _buffer = GC.AllocateArray<byte>(1, pinned: true);
            private TaskCompletionSource<int> _completion = null!;

            public Task<int> Completion => _completion.Task;
            public byte Value => _buffer[0];

            public void Prepare() =>
                _completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override unsafe IoUringRequest Request
            {
                get
                {
                    fixed (byte* buffer = _buffer)
                    {
                        return new IoUringRequest(IoUringOperationKind.Receive, buffer, _buffer.Length);
                    }
                }
            }

            protected override void OnCompleted(int result, uint flags, long sequence)
            {
                TaskCompletionSource<int> completion = _completion;
                CompleteOperation();
                completion.SetResult(result);
            }
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void ReceiveCancellation_RetiresPinsBeforeReuse(bool closeSocket)
        {
            RemoteExecutor.Invoke(async closeText =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (TrackingMemoryManager memory = new TrackingMemoryManager())
                using (CancellationTokenSource cancellation = new CancellationTokenSource())
                {
                    Task<int> receive = receiver.ReceiveAsync(memory.Memory, SocketFlags.None, cancellation.Token).AsTask();
                    Assert.False(receive.IsCompleted);
                    Assert.Equal(1, memory.PinCount);
                    Assert.Equal(0, memory.UnpinCount);
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    cancellation.Cancel();
                    if (bool.Parse(closeText))
                    {
                        receiver.Dispose();
                    }
                    OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => receive.WaitAsync(TestSettings.PassingTestTimeout));
                    Assert.Equal(cancellation.Token, error.CancellationToken);
                    Assert.Equal(1, memory.UnpinCount);
                    memory.GetSpan()[0] = 0xCC;
                    if (!bool.Parse(closeText))
                    {
                        sender.Send(new byte[] { 9 });
                        byte[] next = new byte[1];
                        Assert.Equal(1, await receiver.ReceiveAsync(next.AsMemory()).AsTask().WaitAsync(TestSettings.PassingTestTimeout));
                        Assert.Equal(9, next[0]);
                        Assert.Equal(0xCC, memory.GetSpan()[0]);
                    }
                }
            }, closeSocket.ToString(), CreateOptions(3)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void SocketClose_OnOnlyWorker_DrainsWithoutUnpinningOnIssuer()
        {
            RemoteExecutor.Invoke(async () =>
            {
                ThreadPool.GetMinThreads(out _, out int minIo);
                ThreadPool.GetMaxThreads(out _, out int maxIo);
                Assert.True(ThreadPool.SetMinThreads(1, minIo));
                Assert.True(ThreadPool.SetMaxThreads(1, maxIo));
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (TrackingMemoryManager memory = new TrackingMemoryManager())
                {
                    Task<int>? receive = null;
                    await Task.Run(() =>
                    {
                        receive = receiver.ReceiveAsync(memory.Memory).AsTask();
                        receiver.Dispose();
                        Assert.Equal(0, memory.UnpinCount);
                    }).WaitAsync(TestSettings.PassingTestTimeout);
                    SocketException error = await Assert.ThrowsAsync<SocketException>(
                        () => receive!.WaitAsync(TestSettings.PassingTestTimeout));
                    Assert.Equal(SocketError.OperationAborted, error.SocketErrorCode);
                    Assert.Equal(1, memory.UnpinCount);
                }
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void NetworkStreamWrite_Backpressure_DoesNotCompleteAfterShortSend(bool cancel)
        {
            RemoteExecutor.Invoke(async cancelText =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (NetworkStream stream = new NetworkStream(sender, ownsSocket: false))
                using (CancellationTokenSource cancellation = new CancellationTokenSource())
                {
                    sender.SendBufferSize = 4096;
                    byte[] data = new byte[4 * 1024 * 1024];
                    new Random(42).NextBytes(data);
                    Task write = stream.WriteAsync(data.AsMemory(), cancellation.Token).AsTask();
                    if (bool.Parse(cancelText))
                    {
                        byte[] prefix = new byte[4096];
                        Assert.NotEqual(0, await receiver.ReceiveAsync(prefix.AsMemory()).AsTask()
                            .WaitAsync(TestSettings.PassingTestTimeout));
                        cancellation.Cancel();
                        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                            () => write.WaitAsync(TestSettings.PassingTestTimeout));
                        Assert.Equal(cancellation.Token, error.CancellationToken);
                    }
                    else
                    {
                        byte[] received = new byte[data.Length];
                        Task<int> read = Task.Run(async () =>
                        {
                            int offset = 0;
                            while (offset < received.Length)
                            {
                                int count = await receiver.ReceiveAsync(received.AsMemory(offset)).AsTask()
                                    .WaitAsync(TestSettings.PassingTestTimeout);
                                if (count == 0)
                                {
                                    break;
                                }
                                offset += count;
                            }
                            return offset;
                        });
                        await write.WaitAsync(TestSettings.PassingTestTimeout);
                        sender.Shutdown(SocketShutdown.Send);
                        Assert.Equal(data.Length, await read.WaitAsync(TestSettings.PassingTestTimeout));
                        AssertExtensions.SequenceEqual(data.AsSpan(), received.AsSpan());
                    }
                }
            }, cancel.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void BoundSend_CloseBetweenNativeRequests_RemembersLogicalSend()
        {
            RemoteExecutor.Invoke(() =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (IoRingBoundHandle binding = IoUring.Bind(sender.SafeHandle))
                using (ManualResetEventSlim delivered = new ManualResetEventSlim())
                using (ManualResetEventSlim resume = new ManualResetEventSlim())
                {
                    PausedSendOperation operation = new PausedSendOperation(delivered, resume);
                    binding.Enqueue(operation);
                    try
                    {
                        Assert.True(delivered.Wait(TestSettings.PassingTestTimeout));
                        Assert.True(binding.DisposeAndWait());
                    }
                    finally
                    {
                        resume.Set();
                        Assert.True(operation.Completion.Wait(TestSettings.PassingTestTimeout));
                    }
                    Assert.True(operation.Completion.Result);
                }
            }, CreateOptions(1)).Dispose();
        }

        private sealed class PausedSendOperation : IoUringOperation
        {
            private readonly byte[] _buffer = GC.AllocateArray<byte>(1, pinned: true);
            private readonly ManualResetEventSlim _delivered;
            private readonly ManualResetEventSlim _resume;
            private readonly TaskCompletionSource<bool> _completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public PausedSendOperation(ManualResetEventSlim delivered, ManualResetEventSlim resume)
            {
                _delivered = delivered;
                _resume = resume;
            }

            public Task<bool> Completion => _completion.Task;

            protected override unsafe IoUringRequest Request
            {
                get
                {
                    fixed (byte* buffer = _buffer)
                    {
                        return new IoUringRequest(IoUringOperationKind.Send, buffer, _buffer.Length);
                    }
                }
            }

            protected override void OnCompleted(int result, uint flags, long sequence)
            {
                _delivered.Set();
                bool resumed = _resume.Wait(TestSettings.PassingTestTimeout);
                bool canceled = IsCancellationRequested;
                CompleteOperation();
                _completion.SetResult(resumed && canceled && result == 1);
            }
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(2)]
        [InlineData(1200)]
        public void GatherSend_WaitAll_CompletesOnceAtNativeVectorLimit(int vectorCount)
        {
            RemoteExecutor.Invoke(async countText =>
            {
                int count = int.Parse(countText);
                int bufferSize = count == 2 ? 1024 * 1024 : 2048;
                byte[] data = new byte[count * bufferSize];
                new Random(42).NextBytes(data);
                SendVector[] vectors = new SendVector[count];
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                {
                    sender.SendBufferSize = 4096;
                    GCHandle dataPin = GCHandle.Alloc(data, GCHandleType.Pinned);
                    GCHandle vectorsPin = default;
                    try
                    {
                        for (int i = 0; i < count; i++)
                        {
                            vectors[i].Base = dataPin.AddrOfPinnedObject() + i * bufferSize;
                            vectors[i].Count = (UIntPtr)bufferSize;
                        }
                        vectorsPin = GCHandle.Alloc(vectors, GCHandleType.Pinned);
                        int callbacks = 0;
                        TaskCompletionSource<int> completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                        unsafe
                        {
                            Assert.True(IoUring.TrySubmitSendV(sender.SafeHandle, (void*)vectorsPin.AddrOfPinnedObject(), count, 0, result =>
                            {
                                Interlocked.Increment(ref callbacks);
                                completion.TrySetResult(result);
                            }));
                        }

                        int expected = Math.Min(count, 1024) * bufferSize;
                        byte[] received = new byte[expected];
                        int offset = 0;
                        while (offset < expected)
                        {
                            int read = await receiver.ReceiveAsync(received.AsMemory(offset), SocketFlags.None)
                                .AsTask().WaitAsync(TestSettings.PassingTestTimeout);
                            Assert.NotEqual(0, read);
                            offset += read;
                        }
                        Assert.Equal(expected, await completion.Task.WaitAsync(TestSettings.PassingTestTimeout));
                        Assert.Equal(1, Volatile.Read(ref callbacks));
                        AssertExtensions.SequenceEqual(data.AsSpan(0, expected), received.AsSpan());
                    }
                    finally
                    {
                        // Drain kernel ownership before unpinning, including on assertion failures.
                        sender.Dispose();
                        if (vectorsPin.IsAllocated)
                        {
                            vectorsPin.Free();
                        }
                        dataPin.Free();
                    }
                }
            }, vectorCount.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false, false, 5)]
        [InlineData(false, true, 5)]
        [InlineData(true, false, 5)]
        [InlineData(true, true, 5)]
        [InlineData(false, false, 1200)]
        [InlineData(true, false, 1200)]
        public void BufferListSend_Backpressure_PreservesOffsetsAndPins(bool useEventArgs, bool useIPv6, int vectorCount)
        {
            RemoteExecutor.Invoke(async (eventArgsText, ipv6Text, vectorCountText) =>
            {
                bool eventArgs = bool.Parse(eventArgsText);
                IPAddress address = bool.Parse(ipv6Text) ? IPAddress.IPv6Loopback : IPAddress.Loopback;
                using Socket listener = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(address, 0));
                listener.Listen(1);
                using Socket sender = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                sender.SendBufferSize = 4096;
                sender.Connect(listener.LocalEndPoint!);
                using Socket receiver = listener.Accept();
                receiver.ReceiveBufferSize = 65536;
                using SocketAsyncEventArgs args = new SocketAsyncEventArgs();
                TaskCompletionSource<int>? completion = null;
                args.Completed += (_, completed) =>
                {
                    Assert.Equal(SocketError.Success, completed.SocketError);
                    completion!.SetResult(completed.BytesTransferred);
                };

                for (int iteration = 0; iteration < 3; iteration++)
                {
                    byte[] first = new byte[1024 * 1024 + 13];
                    byte[] second = new byte[1024 * 1024 + 17];
                    new Random(42 + iteration).NextBytes(first);
                    new Random(100 + iteration).NextBytes(second);
                    List<ArraySegment<byte>> buffers = new List<ArraySegment<byte>>
                    {
                        new ArraySegment<byte>(first, 0, 0),
                        new ArraySegment<byte>(first, 3, first.Length - 13),
                        new ArraySegment<byte>(second, 2, 0),
                        new ArraySegment<byte>(second, 7, second.Length - 17),
                        new ArraySegment<byte>(first, first.Length, 0),
                    };
                    while (buffers.Count < int.Parse(vectorCountText))
                    {
                        buffers.Add(new ArraySegment<byte>(first, buffers.Count, 2048));
                    }
                    int length = 0;
                    foreach (ArraySegment<byte> segment in buffers)
                    {
                        length += segment.Count;
                    }
                    Task<int> pending;
                    if (eventArgs)
                    {
                        completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                        args.BufferList = buffers;
                        Assert.True(sender.SendAsync(args));
                        pending = completion.Task;
                    }
                    else
                    {
                        pending = sender.SendAsync(buffers, SocketFlags.None);
                    }
                    Assert.False(pending.IsCompleted);
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

                    byte[] received = new byte[length];
                    Task receiveTask = ReceiveAll();
                    Assert.Equal(length, await pending.WaitAsync(TestSettings.PassingTestTimeout));
                    await receiveTask.WaitAsync(TestSettings.PassingTestTimeout);
                    int offset = 0;
                    foreach (ArraySegment<byte> segment in buffers)
                    {
                        AssertExtensions.SequenceEqual(segment.AsSpan(), received.AsSpan(offset, segment.Count));
                        offset += segment.Count;
                    }

                    async Task ReceiveAll()
                    {
                        int receivedCount = 0;
                        while (receivedCount < received.Length)
                        {
                            int read = await receiver.ReceiveAsync(received.AsMemory(receivedCount), SocketFlags.None);
                            Assert.NotEqual(0, read);
                            receivedCount += read;
                        }
                    }
                }
            }, useEventArgs.ToString(), useIPv6.ToString(), vectorCount.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void BufferListSend_Backpressure_CloseCompletes(bool closeSender)
        {
            RemoteExecutor.Invoke(async closeText =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (SocketAsyncEventArgs args = new SocketAsyncEventArgs())
                {
                    sender.SendBufferSize = 4096;
                    byte[] buffer = new byte[2 * 1024 * 1024];
                    args.BufferList = new List<ArraySegment<byte>>
                    {
                        new ArraySegment<byte>(buffer, 0, buffer.Length / 2),
                        new ArraySegment<byte>(buffer, buffer.Length / 2, buffer.Length / 2),
                    };
                    TaskCompletionSource completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    args.Completed += (_, _) => completion.SetResult();
                    Assert.True(sender.SendAsync(args));
                    Assert.False(completion.Task.IsCompleted);
                    if (bool.Parse(closeText))
                    {
                        sender.Dispose();
                    }
                    else
                    {
                        receiver.LingerState = new LingerOption(true, 0);
                        receiver.Dispose();
                    }
                    await completion.Task.WaitAsync(TestSettings.PassingTestTimeout);
                    Assert.InRange(args.BytesTransferred, 0, buffer.Length - 1);
                    if (args.SocketError == SocketError.Success)
                    {
                        Assert.NotEqual(0, args.BytesTransferred);
                    }
                }
            }, closeSender.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(null, 512)]
        [InlineData("128", 128)]
        [InlineData("256", 256)]
        [InlineData("512", 512)]
        public void MultishotReceive_BufferCount_DefaultAndOverride(string? configuredCount, int expectedCount)
        {
            RemoteInvokeOptions options = CreateOptions(1);
            options.StartInfo.Environment["DOTNET_IORING_RECV_BUFFER_COUNT"] = configuredCount;
            RemoteExecutor.Invoke(expectedText =>
            {
                Assert.True(IoUring.IsSupported);
                Type ioUringType = typeof(object).Assembly.GetType("System.Threading.PortableThreadPool+IoUringThreadPool", throwOnError: true)!;
#pragma warning disable IL2075 // RemoteExecutor runs this implementation-specific test without trimming.
                Array rings = (Array)ioUringType.GetField("s_rings",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
                Assert.Equal(1, rings.Length);
                object ring = rings.GetValue(0)!;
                object pool = ring.GetType().GetField("ReceiveBuffers")!.GetValue(ring)!;
                Assert.Equal(int.Parse(expectedText), (int)pool.GetType().GetField("BufferCount")!.GetValue(pool)!);
#pragma warning restore IL2075
            }, expectedCount.ToString(), options).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void SocketAsyncEngine_CreationDependsOnIoUring(bool useIoUring)
        {
            RemoteInvokeOptions options = CreateOptions(1);
            options.StartInfo.Environment["DOTNET_USE_IO_URING"] = useIoUring ? "1" : "0";
            options.StartInfo.Environment["DOTNET_SYSTEM_NET_SOCKETS_THREAD_COUNT"] = "1";
            RemoteExecutor.Invoke(enabledText =>
            {
                bool enabled = bool.Parse(enabledText);
                using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                Assert.Equal(enabled, IoUring.IsSupported);

                Type engineType = typeof(Socket).Assembly.GetType("System.Net.Sockets.SocketAsyncEngine", throwOnError: true)!;
#pragma warning disable IL2075 // RemoteExecutor runs this implementation-specific test without trimming.
                Array engines = (Array)engineType.GetField("s_engines",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
#pragma warning restore IL2075
                Assert.Equal(enabled ? 0 : 1, engines.Length);
            }, useIoUring.ToString(), options).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void SocketAsyncEngine_FallbackRejectedWhenIoUringEnabled(bool useIoUring)
        {
            RemoteInvokeOptions options = CreateOptions(1);
            options.StartInfo.Environment["DOTNET_USE_IO_URING"] = useIoUring ? "1" : "0";
            RemoteExecutor.Invoke(async enabledText =>
            {
                bool enabled = bool.Parse(enabledText);
                Assert.Equal(enabled, IoUring.IsSupported);
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                {
                    if (enabled)
                    {
                        await Assert.ThrowsAsync<InvalidOperationException>(ReceiveWithPeek);
                    }
                    else
                    {
                        await ReceiveWithPeek();
                    }

                    async Task ReceiveWithPeek()
                    {
                        byte[] buffer = new byte[1];
                        Task<int> pending = receiver.ReceiveAsync(buffer.AsMemory(), SocketFlags.Peek).AsTask();
                        Assert.Equal(1, sender.Send(new byte[] { 42 }));
                        Assert.Equal(1, await pending.WaitAsync(TestSettings.PassingTestTimeout));
                        Assert.Equal(42, buffer[0]);
                    }
                }
            }, useIoUring.ToString(), options).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(1)]
        [InlineData(3)]
        public void PendingAccept_CompletesOnWorker(int ringCount)
        {
            RemoteInvokeOptions options = CreateOptions(ringCount);
            RemoteExecutor.Invoke(() =>
            {
                Assert.True(IoUring.IsSupported);
                using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(1);
                listener.Blocking = false;

                TaskCompletionSource<int> completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                int callerThreadId = Environment.CurrentManagedThreadId;
                unsafe
                {
                    Assert.True(IoUring.TrySubmitAccept(listener.SafeHandle, null, null, 0, result =>
                    {
                        Assert.True(Thread.CurrentThread.IsThreadPoolThread);
                        Assert.NotEqual(callerThreadId, Environment.CurrentManagedThreadId);
                        completion.SetResult(result);
                    }));
                }

                using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                client.Connect(listener.LocalEndPoint!);
                int acceptedFd = completion.Task.WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
                Assert.True(acceptedFd >= 0, $"Accept failed with result {acceptedFd}.");
                using SafeSocketHandle acceptedHandle = new SafeSocketHandle((IntPtr)acceptedFd, ownsHandle: true);
                using Socket accepted = new Socket(acceptedHandle);
                Assert.Equal(client.LocalEndPoint, accepted.RemoteEndPoint);
            }, options).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void MultishotReceive_CompletionQueuePressure_PreservesEveryByte()
        {
            RemoteInvokeOptions options = CreateOptions(1);
            options.StartInfo.Environment["DOTNET_IORING_RECV_BUFFER_SIZE"] = "1";
            options.StartInfo.Environment["DOTNET_IORING_RECV_BUFFER_COUNT"] = "4096";
            RemoteExecutor.Invoke(async () =>
            {
                const int ConnectionCount = 256;
                const int PayloadLength = 256;
                using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(ConnectionCount);
                List<Socket> sockets = new List<Socket>();
                Socket[] receivers = new Socket[ConnectionCount];
                byte[] payload = new byte[PayloadLength];
                Array.Fill(payload, (byte)0x5A);
                try
                {
                    for (int index = 0; index < ConnectionCount; index++)
                    {
                        Socket sender = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        sockets.Add(sender);
                        sender.Connect(listener.LocalEndPoint!);
                        Socket receiver = listener.Accept();
                        sockets.Add(receiver);
                        receivers[index] = receiver;
                        Assert.Equal(payload.Length, sender.Send(payload));
                        sender.Shutdown(SocketShutdown.Send);
                    }

                    Task[] reads = new Task[ConnectionCount];
                    for (int index = 0; index < reads.Length; index++)
                    {
                        Socket receiver = receivers[index];
                        reads[index] = Task.Run(async () =>
                        {
                            int received = 0;
                            await foreach (IMemoryOwner<byte> owner in receiver.ReceiveMultishotAsync())
                            {
                                using (owner)
                                {
                                    Assert.Equal(1, owner.Memory.Length);
                                    Assert.Equal(0x5A, owner.Memory.Span[0]);
                                    received += owner.Memory.Length;
                                }
                            }
                            Assert.Equal(PayloadLength, received);
                        });
                    }

                    await Task.WhenAll(reads).WaitAsync(TestSettings.PassingTestTimeout);
                }
                finally
                {
                    foreach (Socket socket in sockets)
                    {
                        socket.Dispose();
                    }
                }
            }, options).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void MultishotReceive_PositiveTerminalDelivery_StopsAfterCallback(bool closeHandle)
        {
            RemoteExecutor.Invoke(async closeText =>
            {
                bool close = bool.Parse(closeText);
                const int OperationCanceled = 125;
                using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(1);
                using Socket sender = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sender.Connect(listener.LocalEndPoint!);
                using Socket receiver = listener.Accept();
                TaskCompletionSource drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                AsyncLocal<int> context = new AsyncLocal<int>();
                TrackingMemoryManager owner = new TrackingMemoryManager();
                IoUringOperation? operation = null;
                bool testing = false;
                int callbacks = 0;
                Assert.True(IoUring.TrySubmitRecvMultishot(receiver.SafeHandle, (result, buffer, more) =>
                {
                    if (!testing)
                    {
                        buffer?.Dispose();
                        if (!more)
                        {
                            drained.SetResult();
                        }
                        return;
                    }

                    Assert.Equal(0, context.Value);
                    Assert.Null(SynchronizationContext.Current);
                    if (++callbacks == 1)
                    {
                        Assert.Equal(1, result);
                        Assert.Same(owner, buffer);
                        Assert.True(more);
                        buffer!.Dispose();
                        context.Value = 42;
                        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                        if (close)
                        {
                            receiver.Dispose();
                        }
                        else
                        {
                            operation!.RequestCancellation();
                        }
                    }
                    else
                    {
                        Assert.Equal(2, callbacks);
                        Assert.Equal(-OperationCanceled, result);
                        Assert.Null(buffer);
                        Assert.False(more);
                    }
                }, out operation));
                operation!.RequestCancellation();
                await drained.Task.WaitAsync(TestSettings.PassingTestTimeout);

                // Model terminal-data delivery with no native request still holding the handle.
#pragma warning disable IL2075 // The RemoteExecutor process is untrimmed.
                Type operationType = operation.GetType();
                const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(IoUringOperation).GetMethod("Begin", Flags)!.Invoke(operation,
                    new object[] { IoUring.Bind(receiver.SafeHandle), CancellationToken.None });
                System.Reflection.MethodInfo deliver = operationType.GetMethod("Deliver", Flags)!;
#pragma warning restore IL2075
                testing = true;
                await Task.Run(() => deliver.Invoke(operation, new object[] { 1, owner, false, Thread.CurrentThread }))
                    .WaitAsync(TestSettings.PassingTestTimeout);
                Assert.Equal(2, callbacks);
                Assert.Equal(1, owner.DisposeCount);
            }, closeHandle.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(1, 1, false)]
        [InlineData(1, 2048, false)]
        [InlineData(3, 2048, false)]
        [InlineData(1, 64, true)]
        public void PendingReceiveBurst_CompletesEveryOperation(int ringCount, int operationCount, bool blockFirstCallback)
        {
            RemoteInvokeOptions options = CreateOptions(ringCount);
            RemoteExecutor.Invoke((countText, blockText) =>
            {
                Assert.True(IoUring.IsSupported);
                int count = int.Parse(countText);
                bool blockFirst = bool.Parse(blockText);
                using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(1);
                using Socket sender = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sender.Connect(listener.LocalEndPoint!);
                using Socket receiver = listener.Accept();
                receiver.Blocking = false;
                sender.SendTimeout = TestSettings.PassingTestTimeout;

                byte[] received = GC.AllocateArray<byte>(count, pinned: true);
                byte[] sent = new byte[count];
                Array.Fill(sent, (byte)0x5A);
                Task<int>[] completions = new Task<int>[count];
                using ManualResetEventSlim releaseCallback = new ManualResetEventSlim();
                unsafe
                {
                    fixed (byte* pointer = received)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            TaskCompletionSource<int> completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                            completions[i] = completion.Task;
                            Action<int> callback = i == 0 && blockFirst
                                ? result =>
                                {
                                    Assert.True(releaseCallback.Wait(TestSettings.PassingTestTimeout));
                                    completion.SetResult(result);
                                }
                                : completion.SetResult;
                            Assert.True(IoUring.TrySubmitRecv(receiver.SafeHandle, pointer + i, 1, 0, callback));
                        }
                    }
                }

                int offset = 0;
                while (offset < sent.Length)
                {
                    int written = sender.Send(sent.AsSpan(offset));
                    Assert.True(written > 0);
                    offset += written;
                }

                if (blockFirst)
                {
                    try
                    {
                        Task.WhenAll(completions.AsSpan(1).ToArray()).WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
                    }
                    finally
                    {
                        releaseCallback.Set();
                    }
                }

                int[] results = Task.WhenAll(completions).WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
                Assert.All(results, result => Assert.Equal(1, result));
                Assert.Equal(sent, received);
                GC.KeepAlive(received);
            }, operationCount.ToString(), blockFirstCallback.ToString(), options).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(1)]
        [InlineData(3)]
        public void Receive_ReentrantCompletion_ReleasesEveryPin(int ringCount)
        {
            RemoteExecutor.Invoke(() =>
            {
                Assert.True(IoUring.IsSupported);
                using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(1);
                using Socket sender = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sender.Connect(listener.LocalEndPoint!);
                using Socket receiver = listener.Accept();
                sender.SendTimeout = TestSettings.PassingTestTimeout;

                using TrackingMemoryManager memory = new TrackingMemoryManager();
                using SocketAsyncEventArgs args = new SocketAsyncEventArgs();
                args.SetBuffer(memory.Memory);
                byte[] sent = new byte[] { 0x5A };
                TaskCompletionSource completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                const int OperationCount = 2048;
                int remaining = OperationCount;
                args.Completed += (_, result) =>
                {
                    Assert.Equal(SocketError.Success, result.SocketError);
                    Assert.Equal(1, result.BytesTransferred);
                    Assert.Equal(sent[0], memory.GetSpan()[0]);
                    Assert.Equal(memory.PinCount, memory.UnpinCount);
                    if (--remaining == 0)
                    {
                        completion.SetResult();
                        return;
                    }

                    Assert.True(receiver.ReceiveAsync(args));
                    Assert.Equal(1, sender.Send(sent));
                };

                Assert.True(receiver.ReceiveAsync(args));
                Assert.Equal(1, sender.Send(sent));
                completion.Task.WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
                Assert.Equal(OperationCount, memory.PinCount);
                Assert.Equal(OperationCount, memory.UnpinCount);
            }, CreateOptions(ringCount)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(1)]
        [InlineData(3)]
        public void Receive_UnpinSubmitsAnotherReceive_CompletesBoth(int ringCount)
        {
            RemoteExecutor.Invoke(() =>
            {
                Assert.True(IoUring.IsSupported);
                using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(1);
                using Socket sender = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sender.Connect(listener.LocalEndPoint!);
                using Socket receiver = listener.Accept();
                sender.SendTimeout = TestSettings.PassingTestTimeout;

                using TrackingMemoryManager firstMemory = new TrackingMemoryManager();
                using TrackingMemoryManager secondMemory = new TrackingMemoryManager();
                using SocketAsyncEventArgs firstArgs = new SocketAsyncEventArgs();
                using SocketAsyncEventArgs secondArgs = new SocketAsyncEventArgs();
                firstArgs.SetBuffer(firstMemory.Memory);
                secondArgs.SetBuffer(secondMemory.Memory);
                TaskCompletionSource first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                firstArgs.Completed += (_, result) =>
                {
                    Assert.Equal(SocketError.Success, result.SocketError);
                    Assert.Equal(1, result.BytesTransferred);
                    Assert.Equal(0x5A, firstMemory.GetSpan()[0]);
                    first.SetResult();
                };
                secondArgs.Completed += (_, result) =>
                {
                    Assert.Equal(SocketError.Success, result.SocketError);
                    Assert.Equal(1, result.BytesTransferred);
                    Assert.Equal(0x5B, secondMemory.GetSpan()[0]);
                    second.SetResult();
                };
                firstMemory.OnUnpin = () =>
                {
                    Assert.True(receiver.ReceiveAsync(secondArgs));
                    Assert.Equal(1, sender.Send(new byte[] { 0x5B }));
                };

                Assert.True(receiver.ReceiveAsync(firstArgs));
                Assert.Equal(1, sender.Send(new byte[] { 0x5A }));
                Task.WhenAll(first.Task, second.Task).WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
                Assert.Equal(1, firstMemory.PinCount);
                Assert.Equal(1, firstMemory.UnpinCount);
                Assert.Equal(1, secondMemory.PinCount);
                Assert.Equal(1, secondMemory.UnpinCount);
            }, CreateOptions(ringCount)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(1, false)]
        [InlineData(1, true)]
        [InlineData(3, false)]
        [InlineData(3, true)]
        public void PendingReceiveBurst_IsolatesCompletionThreadState(int ringCount, bool useChangeNotifications)
        {
            RemoteExecutor.Invoke(useChangeNotificationsText =>
            {
                Assert.True(IoUring.IsSupported);
                LimitThreadPoolToOneWorker();
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                {
                    receiver.Blocking = false;
                    sender.SendTimeout = TestSettings.PassingTestTimeout;
                    const int OperationCount = 128;
                    byte[] received = GC.AllocateArray<byte>(OperationCount, pinned: true);
                    byte[] sent = new byte[OperationCount];
                    Array.Fill(sent, (byte)0x5A);
                    bool notify = bool.Parse(useChangeNotificationsText);
                    int contextResets = 0;
                    AsyncLocal<int> local = notify ? new AsyncLocal<int>(change =>
                    {
                        if (change.ThreadContextChanged)
                        {
                            Assert.Equal(1, change.PreviousValue);
                            Assert.Equal(0, change.CurrentValue);
                            contextResets++;
                        }
                    }) : new AsyncLocal<int>();
                    SynchronizationContext context = new SynchronizationContext();
                    TaskCompletionSource completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    int completed = 0;
                    string? initialThreadName = null;
                    Action<int> callback = result =>
                    {
                        Assert.Equal(1, result);
                        Assert.Equal(0, local.Value);
                        Assert.Null(SynchronizationContext.Current);
                        if (completed == 0)
                        {
                            initialThreadName = Thread.CurrentThread.Name;
                        }
                        Assert.Equal(initialThreadName, Thread.CurrentThread.Name);
                        local.Value = 1;
                        SynchronizationContext.SetSynchronizationContext(context);
                        Thread.CurrentThread.Name = nameof(PendingReceiveBurst_IsolatesCompletionThreadState);
                        if (++completed == OperationCount)
                        {
                            // Observe cleanup of the final callback after its dispatcher returns.
                            ThreadPool.UnsafeQueueUserWorkItem(_ => completion.SetResult(), null);
                        }
                    };

                    long initialCompletedWorkItemCount = ThreadPool.CompletedWorkItemCount;
                    unsafe
                    {
                        fixed (byte* pointer = received)
                        {
                            for (int i = 0; i < OperationCount; i++)
                            {
                                Assert.True(IoUring.TrySubmitRecv(receiver.SafeHandle, pointer + i, 1, 0, callback));
                            }
                        }
                    }

                    int offset = 0;
                    while (offset < sent.Length)
                    {
                        int written = sender.Send(sent.AsSpan(offset));
                        Assert.True(written > 0);
                        offset += written;
                    }

                    completion.Task.WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
                    Assert.InRange(ThreadPool.CompletedWorkItemCount - initialCompletedWorkItemCount, OperationCount, long.MaxValue);
                    Assert.Equal(notify ? OperationCount : 0, contextResets);
                    Assert.Equal(sent, received);
                    GC.KeepAlive(received);
                }
            }, useChangeNotifications.ToString(), CreateOptions(ringCount)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData("eof")]
        [InlineData("cancel")]
        [InlineData("throw")]
        [InlineData("reset")]
        public void ReceiveMultishotCallbacks_DrainBeforeCompleting(string ending)
        {
            RemoteExecutor.Invoke(ending =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (CancellationTokenSource cancellation = new CancellationTokenSource(TestSettings.PassingTestTimeout))
                {
                    TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    InvalidOperationException failure = new("Callback failed");
                    IMemoryOwner<byte>? retained = null;
                    Task receiving = receiver.ReceiveMultishotAsync(buffer =>
                    {
                        Assert.True(Thread.CurrentThread.IsThreadPoolThread);
                        Assert.Null(retained);
                        retained = buffer;
                        received.TrySetResult();
                        if (ending == "throw")
                        {
                            throw failure;
                        }
                    }, cancellation.Token);

                    byte[] bytes = [1, 2, 3, 4];
                    Assert.Equal(bytes.Length, sender.Send(bytes));
                    received.Task.WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
                    if (ending == "eof")
                    {
                        sender.Shutdown(SocketShutdown.Send);
                        receiving.WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
                    }
                    else if (ending == "cancel")
                    {
                        cancellation.Cancel();
                        OperationCanceledException error = Assert.ThrowsAny<OperationCanceledException>(
                            () => receiving.WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult());
                        Assert.Equal(cancellation.Token, error.CancellationToken);
                    }
                    else if (ending == "throw")
                    {
                        Assert.Same(failure, Assert.Throws<InvalidOperationException>(
                            () => receiving.WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult()));
                    }
                    else
                    {
                        sender.LingerState = new LingerOption(true, 0);
                        sender.Dispose();
                        SocketException error = Assert.Throws<SocketException>(
                            () => receiving.WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult());
                        Assert.Equal(SocketError.ConnectionReset, error.SocketErrorCode);
                    }

                    Assert.NotNull(retained);
                    Assert.Equal(bytes, retained.Memory.ToArray());
                    retained.Dispose();
                }
            }, ending, CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void ReceiveMultishotCallbacks_AlreadyReadyFailure_CancelsWithoutExternalToken()
        {
            RemoteExecutor.Invoke(() =>
            {
                for (int iteration = 0; iteration < 64; iteration++)
                {
                    (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                    using (sender)
                    using (receiver)
                    {
                        Assert.Equal(1, sender.Send(new byte[] { 42 }));
                        InvalidOperationException failure = new("Already-ready callback failed");
                        Task receiving = receiver.ReceiveMultishotAsync(buffer =>
                        {
                            buffer.Dispose();
                            throw failure;
                        });
                        Assert.Same(failure, Assert.Throws<InvalidOperationException>(
                            () => receiving.WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult()));
                    }
                }
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void ReceiveMultishotCallbacks_PreCanceled_DoesNotDeliver()
        {
            RemoteExecutor.Invoke(() =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (CancellationTokenSource cancellation = new())
                {
                    Assert.Throws<ArgumentNullException>(() => { _ = receiver.ReceiveMultishotAsync(null!, cancellation.Token); });
                    cancellation.Cancel();
                    Task receiving = receiver.ReceiveMultishotAsync(_ => Assert.Fail("Unexpected callback"), cancellation.Token);
                    OperationCanceledException error = Assert.ThrowsAny<OperationCanceledException>(
                        () => receiving.GetAwaiter().GetResult());
                    Assert.Equal(cancellation.Token, error.CancellationToken);
                }
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(1)]
        [InlineData(3)]
        public void ReceiveMultishotAsync_StreamsMultipleSends_InOrder(int ringCount)
        {
            RemoteExecutor.Invoke(() =>
            {
                Assert.True(IoUring.IsSupported);
                using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(1);
                using Socket sender = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sender.Connect(listener.LocalEndPoint!);
                using Socket receiver = listener.Accept();
                sender.SendTimeout = TestSettings.PassingTestTimeout;

                const int ChunkCount = 5;
                const int ChunkSize = 16;
                byte[] sent = new byte[ChunkCount * ChunkSize];
                for (int i = 0; i < ChunkCount; i++)
                {
                    Array.Fill(sent, (byte)(i + 1), i * ChunkSize, ChunkSize);
                }

                using CancellationTokenSource cts = new CancellationTokenSource(TestSettings.PassingTestTimeout);
                IAsyncEnumerator<IMemoryOwner<byte>> enumerator = receiver.ReceiveMultishotAsync(cts.Token).GetAsyncEnumerator();

                for (int i = 0; i < ChunkCount; i++)
                {
                    Assert.Equal(ChunkSize, sender.Send(sent.AsSpan(i * ChunkSize, ChunkSize)));
                }

                List<byte> received = new List<byte>();
                while (received.Count < sent.Length)
                {
                    Assert.True(enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult());
                    using IMemoryOwner<byte> buffer = enumerator.Current;
                    received.AddRange(buffer.Memory.Span.ToArray());
                }

                Assert.Equal(sent, received.ToArray());
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }, CreateOptions(ringCount)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(1)]
        [InlineData(3)]
        public void ReceiveMultishotAsync_GracefulShutdown_EndsEnumerationWithoutError(int ringCount)
        {
            RemoteExecutor.Invoke(() =>
            {
                Assert.True(IoUring.IsSupported);
                using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(1);
                using Socket sender = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sender.Connect(listener.LocalEndPoint!);
                using Socket receiver = listener.Accept();

                byte[] sent = new byte[] { 0x2A };
                Assert.Equal(1, sender.Send(sent));
                sender.Shutdown(SocketShutdown.Send);

                using CancellationTokenSource cts = new CancellationTokenSource(TestSettings.PassingTestTimeout);
                IAsyncEnumerator<IMemoryOwner<byte>> enumerator = receiver.ReceiveMultishotAsync(cts.Token).GetAsyncEnumerator();

                Assert.True(enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult());
                using (IMemoryOwner<byte> buffer = enumerator.Current)
                {
                    Assert.Equal(sent, buffer.Memory.ToArray());
                }

                // EOF: enumeration ends cleanly, without throwing.
                Assert.False(enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult());
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }, CreateOptions(ringCount)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(1)]
        [InlineData(3)]
        public void ReceiveMultishotAsync_Cancellation_ThrowsOperationCanceled(int ringCount)
        {
            RemoteExecutor.Invoke(() =>
            {
                Assert.True(IoUring.IsSupported);
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                {
                    using CancellationTokenSource cts = new CancellationTokenSource();
                    IAsyncEnumerator<IMemoryOwner<byte>> enumerator = receiver.ReceiveMultishotAsync(cts.Token).GetAsyncEnumerator();

                    ValueTask<bool> moveNextTask = enumerator.MoveNextAsync();
                    cts.Cancel();

                    OperationCanceledException exception = Assert.Throws<OperationCanceledException>(() =>
                        moveNextTask.AsTask().WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult());
                    Assert.Equal(cts.Token, exception.CancellationToken);
                    enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }, CreateOptions(ringCount)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(1, 24)]
        [InlineData(3, 24)]
        public void ReceiveMultishotAsync_ManyConcurrentConnections_PreserveOrderWithSingleWriterChannel(int ringCount, int connectionCount)
        {
            // Regression test for the multishot receive channel's SingleWriter = true setting: with
            // few rings shared by many connections, bursts of unrelated sockets' completions are very
            // likely to land in the same io_uring_enter call. Each connection's own
            // MultishotReceiveOperation guarantees at most one active drainer delivering its
            // completions at a time, in the exact order its ring's single issuer thread enqueued them
            // (see EnqueueFromIssuer/Execute in PortableThreadPool.IoUring.Receive.Unix.cs). If that
            // guarantee ever broke down, this reliably surfaces it as out-of-order or corrupted
            // per-connection data, or a channel-internal exception, rather than a rare/flaky hang.
            RemoteInvokeOptions options = CreateOptions(ringCount);
            RemoteExecutor.Invoke(connectionCountText =>
            {
                Assert.True(IoUring.IsSupported);
                int connections = int.Parse(connectionCountText);
                const int ChunkCount = 500;

                Socket[] senders = new Socket[connections];
                Socket[] receivers = new Socket[connections];
                byte[][] expected = new byte[connections][];
                try
                {
                    Random random = new Random(42);
                    for (int c = 0; c < connections; c++)
                    {
                        (senders[c], receivers[c]) = SocketTestExtensions.CreateConnectedSocketPair();
                        byte[] data = new byte[ChunkCount];
                        random.NextBytes(data);
                        expected[c] = data;
                    }

                    Task[] sendTasks = new Task[connections];
                    for (int c = 0; c < connections; c++)
                    {
                        int idx = c;
                        sendTasks[idx] = Task.Run(() =>
                        {
                            Socket sender = senders[idx];
                            byte[] data = expected[idx];
                            // One byte at a time, as fast as possible, so each connection produces a
                            // long run of individually-completed reads instead of a single big one.
                            for (int i = 0; i < data.Length; i++)
                            {
                                Assert.Equal(1, sender.Send(data, i, 1, SocketFlags.None));
                            }
                        });
                    }

                    Task<byte[]>[] receiveTasks = new Task<byte[]>[connections];
                    for (int c = 0; c < connections; c++)
                    {
                        int idx = c;
                        receiveTasks[idx] = Task.Run(async () =>
                        {
                            Socket receiver = receivers[idx];
                            using CancellationTokenSource cts = new CancellationTokenSource(TestSettings.PassingTestTimeout);
                            List<byte> received = new List<byte>(expected[idx].Length);
                            await foreach (IMemoryOwner<byte> buffer in receiver.ReceiveMultishotAsync(cts.Token))
                            {
                                using (buffer)
                                {
                                    received.AddRange(buffer.Memory.Span.ToArray());
                                }

                                if (received.Count >= expected[idx].Length)
                                {
                                    break;
                                }
                            }

                            return received.ToArray();
                        });
                    }

                    Task.WhenAll(sendTasks).WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
                    byte[][] results = Task.WhenAll(receiveTasks).WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();

                    for (int c = 0; c < connections; c++)
                    {
                        Assert.Equal(expected[c], results[c]);
                    }
                }
                finally
                {
                    foreach (Socket s in senders)
                    {
                        s?.Dispose();
                    }

                    foreach (Socket r in receivers)
                    {
                        r?.Dispose();
                    }
                }
            }, connectionCount.ToString(), options).Dispose();
        }

        [ConditionalFact(nameof(IsRemoteExecutorSupported))]
        public void ReceiveMultishotAsync_NotSupported_ThrowsInvalidOperationException()
        {
            RemoteExecutor.Invoke(() =>
            {
                Assert.False(IoUring.IsSupported);
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                {
                    Assert.Throws<InvalidOperationException>(() => receiver.ReceiveMultishotAsync());
                }
            }, new RemoteInvokeOptions { StartInfo = { Environment = { ["DOTNET_USE_IO_URING"] = "0" } } }).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void Multishot_BufferedCompletionsCrossQueueSegments()
        {
            RemoteExecutor.Invoke(() =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (ManualResetEventSlim firstCallback = new())
                using (ManualResetEventSlim releaseCallback = new())
                {
                    TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    int callbacks = 0;
                    Assert.True(IoUring.TrySubmitRecvMultishot(receiver.SafeHandle, (result, buffer, more) =>
                    {
                        try
                        {
                            if (buffer is not null)
                            {
                                Assert.Equal(result, buffer.Memory.Length);
                                if (callbacks++ == 0)
                                {
                                    Assert.Equal(1, buffer.Memory.Length);
                                    Assert.Equal(0x5A, buffer.Memory.Span[0]);
                                    firstCallback.Set();
                                    Assert.True(releaseCallback.Wait(TestSettings.PassingTestTimeout));
                                    Assert.Equal(1, buffer.Memory.Length);
                                    Assert.Equal(0x5A, buffer.Memory.Span[0]);
                                }
                                else
                                {
                                    Assert.True(buffer.Memory.Span.IndexOfAnyExcept((byte)0) < 0);
                                }
                            }
                            if (!more)
                            {
                                Assert.True(callbacks > 32);
                                completed.TrySetResult();
                            }
                        }
                        catch (Exception error)
                        {
                            completed.TrySetException(error);
                        }
                        finally
                        {
                            buffer?.Dispose();
                        }
                    }, out IoUringOperation? operation));
                    try
                    {
                        Assert.Equal(1, sender.Send(new byte[] { 0x5A }));
                        Assert.True(firstCallback.Wait(TestSettings.PassingTestTimeout));
                        byte[] bytes = new byte[40 * 16384];
                        int sent = 0;
                        while (sent < bytes.Length)
                        {
                            sent += sender.Send(bytes.AsSpan(sent));
                        }

                        object queue = operation!.GetType().GetField("_pending",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(operation)!;
#pragma warning disable IL2075 // RemoteExecutor runs this implementation-specific test without trimming.
                        System.Reflection.PropertyInfo count = queue.GetType().GetProperty("Count")!;
#pragma warning restore IL2075
                        Assert.True(SpinWait.SpinUntil(() => (int)count.GetValue(queue)! > 32, TestSettings.PassingTestTimeout));
                        operation.RequestCancellation();
                    }
                    finally
                    {
                        releaseCallback.Set();
                    }
                    completed.Task.WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
                }
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void Multishot_RetainedBufferDoesNotChangeWhenOtherBuffersCycle()
        {
            RemoteInvokeOptions options = CreateOptions(1);
            options.StartInfo.Environment["DOTNET_IORING_RECV_BUFFER_COUNT"] = "4";
            options.StartInfo.Environment["DOTNET_IORING_RECV_BUFFER_SIZE"] = "128";
            RemoteExecutor.Invoke(async () =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (CancellationTokenSource cancellation = new(TestSettings.PassingTestTimeout))
                {
                    await using IAsyncEnumerator<IMemoryOwner<byte>> reader =
                        receiver.ReceiveMultishotAsync(cancellation.Token).GetAsyncEnumerator();
                    byte[] first = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
                    Assert.Equal(first.Length, sender.Send(first));
                    Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TestSettings.PassingTestTimeout));
                    using IMemoryOwner<byte> retained = reader.Current;
                    byte[] next = new byte[1];
                    for (int index = 0; index < 128; index++)
                    {
                        next[0] = (byte)index;
                        Assert.Equal(1, sender.Send(next));
                        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TestSettings.PassingTestTimeout));
                        using (IMemoryOwner<byte> current = reader.Current)
                        {
                            Assert.Equal(1, current.Memory.Length);
                            Assert.Equal(next[0], current.Memory.Span[0]);
                        }
                        Assert.Equal(first, retained.Memory.ToArray());
                    }
                    await reader.DisposeAsync();
                    receiver.Dispose();
                    Assert.Equal(first, retained.Memory.ToArray());
                }
            }, options).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        [OuterLoop]
        public void Multishot_RepeatedPendingReceiveBursts_DoNotLoseWakeup()
        {
            RemoteExecutor.Invoke(async () =>
            {
                const int ConnectionCount = 64;
                const int BatchCount = 15000;
                Socket[] senders = new Socket[ConnectionCount];
                Socket[] receivers = new Socket[ConnectionCount];
                IAsyncEnumerator<IMemoryOwner<byte>>[] readers = new IAsyncEnumerator<IMemoryOwner<byte>>[ConnectionCount];
                ValueTask<bool>[] pending = new ValueTask<bool>[ConnectionCount];
                byte[] payload = new byte[128];
                using CancellationTokenSource cancellation = new(TestSettings.PassingTestTimeout);
                try
                {
                    for (int i = 0; i < ConnectionCount; i++)
                    {
                        (senders[i], receivers[i]) = SocketTestExtensions.CreateConnectedSocketPair();
                        senders[i].NoDelay = true;
                        readers[i] = receivers[i].ReceiveMultishotAsync(cancellation.Token).GetAsyncEnumerator();
                    }

                    for (int batch = 0; batch < BatchCount; batch++)
                    {
                        for (int i = 0; i < ConnectionCount; i++)
                        {
                            pending[i] = readers[i].MoveNextAsync();
                        }
                        foreach (Socket sender in senders)
                        {
                            Assert.Equal(payload.Length, sender.Send(payload));
                        }
                        for (int i = 0; i < ConnectionCount; i++)
                        {
                            int received = 0;
                            do
                            {
                                Assert.True(await pending[i]);
                                using (IMemoryOwner<byte> owner = readers[i].Current)
                                {
                                    received += owner.Memory.Length;
                                    Assert.True(owner.Memory.Span.IndexOfAnyExcept((byte)0) < 0);
                                }
                                if (received < payload.Length)
                                {
                                    pending[i] = readers[i].MoveNextAsync();
                                }
                            } while (received < payload.Length);
                            Assert.Equal(payload.Length, received);
                        }
                        cancellation.CancelAfter(TestSettings.PassingTestTimeout);
                    }
                }
                finally
                {
                    cancellation.Cancel();
                    for (int i = 0; i < ConnectionCount; i++)
                    {
                        senders[i]?.Dispose();
                        receivers[i]?.Dispose();
                        if (readers[i] is not null)
                        {
                            await readers[i].DisposeAsync();
                        }
                    }
                }
            }, CreateOptions(3)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void Multishot_EarlyBreakDrainsQueuedBuffers()
        {
            RemoteInvokeOptions options = CreateOptions(1);
            options.StartInfo.Environment["DOTNET_IORING_RECV_BUFFER_COUNT"] = "4";
            RemoteExecutor.Invoke(async () =>
            {
                LimitThreadPoolToOneWorker();
                byte[] bytes = new byte[65536];
                for (int iteration = 0; iteration < 16; iteration++)
                {
                    (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                    using (sender)
                    using (receiver)
                    using (CancellationTokenSource cancellation = new(TestSettings.PassingTestTimeout))
                    {
                        int sent = 0;
                        while (sent < bytes.Length)
                        {
                            sent += sender.Send(bytes.AsSpan(sent));
                        }
                        await foreach (IMemoryOwner<byte> owner in receiver.ReceiveMultishotAsync(cancellation.Token))
                        {
                            owner.Dispose();
                            break;
                        }
                    }
                }
            }, options).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(1)]
        [InlineData(3)]
        public void Multishot_CallbacksIsolateThreadState(int ringCount)
        {
            RemoteExecutor.Invoke(() =>
            {
                LimitThreadPoolToOneWorker();
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (ManualResetEventSlim firstCallback = new())
                using (ManualResetEventSlim releaseCallback = new())
                {
                    AsyncLocal<int> context = new();
                    TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    int callbacks = 0;
                    Assert.True(IoUring.TrySubmitRecvMultishot(receiver.SafeHandle, (result, buffer, more) =>
                    {
                        try
                        {
                            Assert.True(Thread.CurrentThread.IsThreadPoolThread);
                            Assert.Equal(0, context.Value);
                            Assert.Null(SynchronizationContext.Current);
                            context.Value = 99;
                            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                            if (callbacks++ == 0)
                            {
                                firstCallback.Set();
                                Assert.True(releaseCallback.Wait(TestSettings.PassingTestTimeout));
                            }
                            if (!more)
                            {
                                Assert.Equal(0, result);
                                Assert.True(callbacks >= 2);
                                completed.TrySetResult();
                            }
                        }
                        catch (Exception error)
                        {
                            completed.TrySetException(error);
                        }
                        finally
                        {
                            buffer?.Dispose();
                        }
                    }, out IoUringOperation? operation));
                    try
                    {
                        sender.Send(new byte[] { 42 });
                        Assert.True(firstCallback.Wait(TestSettings.PassingTestTimeout));
                        sender.Shutdown(SocketShutdown.Send);
                    }
                    finally
                    {
                        releaseCallback.Set();
                    }
                    completed.Task.WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
                }
            }, CreateOptions(ringCount)).Dispose();
        }

        private static void LimitThreadPoolToOneWorker()
        {
            ThreadPool.GetMinThreads(out _, out int completionPortThreads);
            Assert.True(ThreadPool.SetMinThreads(1, completionPortThreads));
            ThreadPool.GetMaxThreads(out _, out completionPortThreads);
            Assert.True(ThreadPool.SetMaxThreads(1, completionPortThreads));
        }

        private sealed class TrackingMemoryManager : MemoryManager<byte>
        {
            private readonly byte[] _buffer = new byte[1];
            public int PinCount;
            public int UnpinCount;
            public int DisposeCount;
            public Action? OnUnpin;

            public override Span<byte> GetSpan() => _buffer;

            public override unsafe MemoryHandle Pin(int elementIndex = 0)
            {
                Assert.Equal(0, elementIndex);
                GCHandle handle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
                Interlocked.Increment(ref PinCount);
                return new MemoryHandle((void*)handle.AddrOfPinnedObject(), handle, this);
            }

            public override void Unpin()
            {
                Interlocked.Increment(ref UnpinCount);
                Interlocked.Exchange(ref OnUnpin, null)?.Invoke();
            }

            protected override void Dispose(bool disposing)
            {
                Assert.Equal(PinCount, UnpinCount);
                DisposeCount++;
            }
        }

        private static RemoteInvokeOptions CreateOptions(int ringCount)
        {
            RemoteInvokeOptions options = new RemoteInvokeOptions();
            options.StartInfo.Environment["DOTNET_USE_IO_URING"] = "1";
            options.StartInfo.Environment["DOTNET_IORING_THREAD_COUNT"] = ringCount.ToString();
            return options;
        }
    }
}
