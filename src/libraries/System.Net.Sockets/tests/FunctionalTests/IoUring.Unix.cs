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
                const int BadFileDescriptor = 9;
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
                IIoUringOperation? operation = null;
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
                        Assert.Equal(close ? -BadFileDescriptor : -OperationCanceled, result);
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
                operationType.GetField("_finished", Flags)!.SetValue(operation, false);
                operationType.GetField("_cancelRequested", Flags)!.SetValue(operation, 0);
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
                    }, out IIoUringOperation? operation));
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
                    }, out IIoUringOperation? operation));
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
