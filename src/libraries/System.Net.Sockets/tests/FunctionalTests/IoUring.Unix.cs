// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
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

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(1, true)]
        [InlineData(3, true)]
        [InlineData(1, false)]
        [InlineData(3, false)]
        public void MultishotReceive_OrderedLeasesAndEof(int ringCount, bool parallelDispatch)
        {
            RemoteInvokeOptions options = CreateOptions(ringCount);
            options.StartInfo.Environment["DOTNET_IORING_PARALLELIZED_ENQUEUE"] = parallelDispatch ? "1" : "0";
            RemoteExecutor.Invoke(() =>
            {
                using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(1);
                using Socket sender = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sender.Connect(listener.LocalEndPoint!);
                using Socket receiver = listener.Accept();
                using SemaphoreSlim received = new(0);
                TaskCompletionSource terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                int count = 0;
                int active = 0;
                Assert.True(IoUring.TrySubmitRecvMultishot(receiver.SafeHandle, (result, owner, more) =>
                {
                    Assert.True(Thread.CurrentThread.IsThreadPoolThread);
                    Assert.Equal(1, Interlocked.Increment(ref active));
                    if (more)
                    {
                        Assert.Equal(1, result);
                        Assert.NotNull(owner);
                        Assert.Equal(1, owner.Memory.Length);
                        Assert.Equal((byte)count++, owner.Memory.Span[0]);
                        owner.Dispose();
                        owner.Dispose();
                        received.Release();
                    }
                    else
                    {
                        Assert.Equal(0, result);
                        Assert.Null(owner);
                        Assert.Equal(128, count);
                        terminal.SetResult();
                    }
                    Interlocked.Decrement(ref active);
                }));
                for (int i = 0; i < 128; i++)
                {
                    Assert.Equal(1, sender.Send(new byte[] { (byte)i }));
                    Assert.True(received.Wait(TestSettings.PassingTestTimeout));
                }
                sender.Shutdown(SocketShutdown.Send);
                terminal.Task.WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
            }, options).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void MultishotReceive_RingExhaustionResumesOnAnotherConnectionsReturn(bool cancelHead)
        {
            RemoteExecutor.Invoke(headText =>
            {
                bool cancelHead = bool.Parse(headText);
                const int ConnectionCount = 18;
                using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(ConnectionCount);
                Socket[] senders = new Socket[ConnectionCount];
                Socket[] receivers = new Socket[ConnectionCount];
                SemaphoreSlim[] signals = new SemaphoreSlim[ConnectionCount];
                TaskCompletionSource[] terminals = new TaskCompletionSource[ConnectionCount];
                List<IMemoryOwner<byte>>[] owners = new List<IMemoryOwner<byte>>[ConnectionCount];
                try
                {
                    for (int i = 0; i < ConnectionCount; i++)
                    {
                        senders[i] = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        senders[i].Connect(listener.LocalEndPoint!);
                        receivers[i] = listener.Accept();
                        signals[i] = new SemaphoreSlim(0);
                        terminals[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        owners[i] = new List<IMemoryOwner<byte>>();
                        int index = i;
                        Assert.True(IoUring.TrySubmitRecvMultishot(receivers[i].SafeHandle, (result, owner, more) =>
                        {
                            if (more)
                            {
                                Assert.Equal(1, result);
                                Assert.NotNull(owner);
                                owners[index].Add(owner);
                                signals[index].Release();
                            }
                            else
                            {
                                Assert.True(result < 0);
                                terminals[index].SetResult();
                            }
                        }));
                        if (i < 16)
                        {
                            for (int j = 0; j < 64; j++)
                            {
                                Assert.Equal(1, senders[i].Send(new byte[] { (byte)i }));
                                Assert.True(signals[i].Wait(TestSettings.PassingTestTimeout));
                            }
                        }
                    }
                    Assert.Equal(1, senders[16].Send(new byte[] { 99 }));
                    Assert.False(signals[16].Wait(100));
                    Assert.Equal(1, senders[17].Send(new byte[] { 100 }));
                    Assert.False(signals[17].Wait(100));
                    int canceled = cancelHead ? 16 : 17;
                    int resumed = cancelHead ? 17 : 16;
                    if (cancelHead)
                    {
                        Type engine = typeof(IoUring).Assembly.GetType("System.Threading.PortableThreadPool+IoUringThreadPool", throwOnError: true)!;
                        System.Collections.IDictionary operations = (System.Collections.IDictionary)engine.GetField(
                            "s_receiveOperations", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                        object operation;
                        lock (operations)
                        {
                            operation = operations[receivers[canceled].SafeHandle]!;
                        }
                        Type operationType = typeof(IoUring).Assembly.GetType(
                            "System.Threading.PortableThreadPool+IoUringThreadPool+MultishotReceiveOperation", throwOnError: true)!;
                        FieldInfo waiting = operationType.GetField("_waitingForBuffers", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        Assert.True(SpinWait.SpinUntil(() => (bool)waiting.GetValue(operation)!, TestSettings.PassingTestTimeout),
                            "The head operation did not wait for buffers.");
                        // Pause cancellation between its stop flag and update publication.
                        operationType.GetField("_stopRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(operation, true);
                    }
                    else
                    {
                        Assert.True(IoUring.TryCancelRecvMultishot(receivers[canceled].SafeHandle));
                        terminals[canceled].Task.WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
                    }
                    owners[0][0].Dispose();
                    terminals[canceled].Task.WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
                    Assert.True(signals[resumed].Wait(TestSettings.PassingTestTimeout),
                        "A canceled head waiter consumed the only buffer-return wakeup.");
                    Assert.Equal(cancelHead ? 100 : 99, owners[resumed][0].Memory.Span[0]);
                    for (int i = 0; i < ConnectionCount; i++)
                    {
                        Assert.Equal(i != canceled, IoUring.TryCancelRecvMultishot(receivers[i].SafeHandle));
                        terminals[i].Task.WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
                        foreach (IMemoryOwner<byte> owner in owners[i])
                        {
                            owner.Dispose();
                        }
                    }
                }
                finally
                {
                    for (int i = 0; i < ConnectionCount; i++)
                    {
                        if (receivers[i] is Socket receiver)
                        {
                            IoUring.TryCancelRecvMultishot(receiver.SafeHandle);
                            receiver.Dispose();
                        }
                        senders[i]?.Dispose();
                        signals[i]?.Dispose();
                    }
                }
            }, cancelHead.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(1)]
        [InlineData(3)]
        public void MultishotReceive_ImmediateCancellationAndCompactionReuseSocket(int ringCount)
        {
            RemoteExecutor.Invoke(() =>
            {
                using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(1);
                using Socket sender = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sender.Connect(listener.LocalEndPoint!);
                using Socket receiver = listener.Accept();
                for (int i = 0; i < 128; i++)
                {
                    TaskCompletionSource terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    Assert.True(IoUring.TrySubmitRecvMultishot(receiver.SafeHandle, (result, owner, more) =>
                    {
                        Assert.Null(owner);
                        Assert.False(more);
                        Assert.True(result < 0);
                        terminal.SetResult();
                    }));
                    Assert.True(IoUring.TryCancelRecvMultishot(receiver.SafeHandle));
                    terminal.Task.WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
                    if (i % 16 == 0)
                    {
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                    }
                }
            }, CreateOptions(ringCount)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void MultishotReceive_CancellationRacesWithPublication()
        {
            RemoteExecutor.Invoke(async () =>
            {
                using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(1);
                using Socket sender = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sender.Connect(listener.LocalEndPoint!);
                using Socket receiver = listener.Accept();
                for (int i = 0; i < 1024; i++)
                {
                    TaskCompletionSource terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    Task<bool> submission = Task.Run(() => IoUring.TrySubmitRecvMultishot(receiver.SafeHandle, (result, owner, more) =>
                    {
                        Assert.Null(owner);
                        Assert.False(more);
                        Assert.True(result < 0);
                        terminal.SetResult();
                    }));
                    Assert.True(SpinWait.SpinUntil(() => IoUring.TryCancelRecvMultishot(receiver.SafeHandle), TestSettings.PassingTestTimeout));
                    Assert.True(await submission);
                    await terminal.Task.WaitAsync(TestSettings.PassingTestTimeout);
                }
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(1, true)]
        [InlineData(3, true)]
        [InlineData(1, false)]
        [InlineData(3, false)]
        public void MultishotAccept_UsesMultipleCompletionsAndTargetedCancellation(int ringCount, bool parallelDispatch)
        {
            RemoteInvokeOptions options = CreateOptions(ringCount);
            options.StartInfo.Environment["DOTNET_IORING_PARALLELIZED_ENQUEUE"] = parallelDispatch ? "1" : "0";
            RemoteExecutor.Invoke(() =>
            {
                using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(32);
                using SemaphoreSlim received = new(0);
                TaskCompletionSource terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                int acceptedCount = 0;
                int activeCallbacks = 0;
                Assert.True(IoUring.TrySubmitAcceptMultishot(listener.SafeHandle, (result, more) =>
                {
                    Assert.True(Thread.CurrentThread.IsThreadPoolThread);
                    Assert.Equal(1, Interlocked.Increment(ref activeCallbacks));
                    if (result >= 0)
                    {
                        using SafeSocketHandle accepted = new((IntPtr)result, ownsHandle: true);
                        Assert.True(more);
                        Interlocked.Increment(ref acceptedCount);
                        received.Release();
                    }
                    else
                    {
                        Assert.False(more);
                        Assert.Equal(32, Volatile.Read(ref acceptedCount));
                        terminal.SetResult();
                    }
                    Interlocked.Decrement(ref activeCallbacks);
                }));

                for (int i = 0; i < 32; i++)
                {
                    using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    client.Connect(listener.LocalEndPoint!);
                    Assert.True(received.Wait(TestSettings.PassingTestTimeout));
                }
                Assert.True(IoUring.TryCancelAcceptMultishot(listener.SafeHandle));
                terminal.Task.WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
                Assert.False(IoUring.TryCancelAcceptMultishot(listener.SafeHandle));
            }, options).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(1)]
        [InlineData(3)]
        public void MultishotAccept_ImmediateCancellation_ReusesListener(int ringCount)
        {
            RemoteExecutor.Invoke(() =>
            {
                using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(1);
                for (int i = 0; i < 128; i++)
                {
                    TaskCompletionSource terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    Assert.True(IoUring.TrySubmitAcceptMultishot(listener.SafeHandle, (result, more) =>
                    {
                        Assert.False(more);
                        Assert.True(result < 0);
                        terminal.SetResult();
                    }));
                    Assert.True(IoUring.TryCancelAcceptMultishot(listener.SafeHandle));
                    terminal.Task.WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
                }
            }, CreateOptions(ringCount)).Dispose();
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

            protected override void Dispose(bool disposing) => Assert.Equal(PinCount, UnpinCount);
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
