// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.DotNet.XUnitExtensions;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace System.Net.Sockets.Tests
{
    public class IoUringTests
    {
        public static bool IsSupported => RemoteExecutor.IsSupported && IoUring.IsSupported;
        public static bool IsRemoteExecutorSupported => RemoteExecutor.IsSupported;

        [Theory]
        [InlineData(new int[0], new[] { 0 }, new[] { 0 })]
        [InlineData(new[] { 0, 0 }, new[] { 0 }, new[] { 2 })]
        [InlineData(new[] { 0, 10, 0, 20, 0 }, new[] { 0, 3, 7, 5, 15 }, new[] { 1, 1, 3, 3, 5 })]
        [InlineData(new[] { 10, 20, 30 }, new[] { 25, 35 }, new[] { 1, 3 })]
        public unsafe void AdvanceIOVectors_PreservesRemainingWindow(int[] lengths, int[] advances, int[] expectedIndices)
        {
            Interop.Sys.IOVector[] vectors = new Interop.Sys.IOVector[lengths.Length];
            for (int i = 0; i < vectors.Length; i++)
            {
                vectors[i].Base = (byte*)0x10000;
                vectors[i].Count = (nuint)lengths[i];
            }

            int index = 0;
            int total = 0;
            for (int i = 0; i < advances.Length; i++)
            {
                index += Interop.Sys.AdvanceIOVectors(vectors.AsSpan(index), advances[i]);
                total += advances[i];
                Assert.Equal(expectedIndices[i], index);
                int consumedPrefix = 0;
                for (int j = 0; j < index; j++)
                {
                    consumedPrefix += lengths[j];
                }
                if (index < vectors.Length)
                {
                    int offset = total - consumedPrefix;
                    Assert.Equal((nuint)(0x10000 + offset), (nuint)vectors[index].Base);
                    Assert.Equal((nuint)(lengths[index] - offset), vectors[index].Count);
                }
                else
                {
                    Assert.Equal(consumedPrefix, total);
                }
            }
        }

        [Fact]
        public unsafe void AdvanceIOVectors_AcceptsByteCountsLargerThanInt32()
        {
            Interop.Sys.IOVector[] vectors =
            [
                new() { Base = (byte*)0x10000, Count = int.MaxValue },
                new() { Base = (byte*)0x10000, Count = int.MaxValue },
                new() { Base = (byte*)0x10000, Count = 10 }
            ];
            int index = Interop.Sys.AdvanceIOVectors(vectors, 2L * int.MaxValue + 3);
            Assert.Equal(2, index);
            Assert.Equal((nuint)0x10003, (nuint)vectors[index].Base);
            Assert.Equal((nuint)7, vectors[index].Count);
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void FileWrite_PartialCompletionKeepsPinsUntilTerminal(bool vector, bool cancel)
        {
            RemoteExecutor.Invoke(async (vectorText, cancelText) =>
            {
                bool useVectors = bool.Parse(vectorText);
                bool cancelWrite = bool.Parse(cancelText);
                SafeFileHandle.CreateAnonymousPipe(out SafeFileHandle reader, out SafeFileHandle writer,
                    asyncRead: true, asyncWrite: true);
                using (reader)
                using (writer)
                using (TrackingMemoryManager memory = new TrackingMemoryManager(2 * 1024 * 1024))
                using (CancellationTokenSource cancellation = new CancellationTokenSource())
                {
                    new Random(42).NextBytes(memory.GetSpan());
                    ReadOnlyMemory<byte>[] buffers = [ReadOnlyMemory<byte>.Empty, memory.Memory, ReadOnlyMemory<byte>.Empty];
                    Task write = useVectors
                        ? RandomAccess.WriteAsync(writer, buffers, 0, cancellation.Token).AsTask()
                        : RandomAccess.WriteAsync(writer, memory.Memory, 0, cancellation.Token).AsTask();
                    byte[] received = new byte[memory.Memory.Length];
                    int read = await RandomAccess.ReadAsync(reader, received.AsMemory(0, 4096), 0);
                    Assert.InRange(read, 1, 4096);
                    Assert.False(write.IsCompleted);
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    if (cancelWrite)
                    {
                        cancellation.Cancel();
                        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                            () => write.WaitAsync(TestSettings.PassingTestTimeout));
                        Assert.Equal(cancellation.Token, error.CancellationToken);
                    }
                    else
                    {
                        while (read < received.Length)
                        {
                            int count = await RandomAccess.ReadAsync(reader, received.AsMemory(read), 0)
                                .AsTask().WaitAsync(TestSettings.PassingTestTimeout);
                            Assert.True(count > 0);
                            read += count;
                        }
                        await write.WaitAsync(TestSettings.PassingTestTimeout);
                        AssertExtensions.SequenceEqual(memory.GetSpan(), received.AsSpan());
                    }
                    Assert.Equal(1, memory.PinCount);
                    Assert.Equal(1, memory.UnpinCount);

                    // Drain canceled-write data while reusing the cached source with a new scalar pin.
                    byte[] next = [42, 43, 44, 45];
                    byte[] remainder = new byte[received.Length + next.Length];
                    Task<int> draining = DrainRemainder();
                    await RandomAccess.WriteAsync(writer, next, 0).AsTask().WaitAsync(TestSettings.PassingTestTimeout);
                    writer.Dispose();
                    int drained = await draining.WaitAsync(TestSettings.PassingTestTimeout);
                    Assert.True(drained >= next.Length);
                    AssertExtensions.SequenceEqual(next.AsSpan(), remainder.AsSpan(drained - next.Length, next.Length));
                    AssertExtensions.SequenceEqual(memory.GetSpan().Slice(read, drained - next.Length),
                        remainder.AsSpan(0, drained - next.Length));

                    async Task<int> DrainRemainder()
                    {
                        int total = 0;
                        int count;
                        while ((count = await RandomAccess.ReadAsync(reader, remainder.AsMemory(total), 0)) != 0)
                        {
                            total += count;
                        }
                        return total;
                    }
                }
            }, vector.ToString(), cancel.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void FileStream_PartialWriteFailurePreservesPositionAndPin()
        {
            RemoteExecutor.Invoke(async () =>
            {
                const int FileSizeLimit = 4096;
                const int SigXfsz = 25; // Linux file-size-limit signal.
                string path = Path.GetTempFileName();
                Assert.Equal(0, Interop.Sys.GetRLimit(Interop.Sys.RlimitResources.RLIMIT_FSIZE, out Interop.Sys.RLimit original));
                using PosixSignalRegistration signal = PosixSignalRegistration.Create((PosixSignal)SigXfsz,
                    context => context.Cancel = true);
                try
                {
                    using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                        FileShare.ReadWrite, bufferSize: 1, FileOptions.Asynchronous);
                    stream.Position = 7;
                    using TrackingMemoryManager memory = new TrackingMemoryManager(16384);
                    new Random(42).NextBytes(memory.GetSpan());
                    Interop.Sys.RLimit limited = original;
                    limited.CurrentLimit = FileSizeLimit;
                    Assert.Equal(0, Interop.Sys.SetRLimit(Interop.Sys.RlimitResources.RLIMIT_FSIZE, ref limited));
                    try
                    {
                        // The first native write reaches the limit; its continuation fails with EFBIG.
                        await Assert.ThrowsAsync<ArgumentOutOfRangeException>("value", () =>
                            stream.WriteAsync(memory.Memory).AsTask().WaitAsync(TestSettings.PassingTestTimeout));
                    }
                    finally
                    {
                        Assert.Equal(0, Interop.Sys.SetRLimit(Interop.Sys.RlimitResources.RLIMIT_FSIZE, ref original));
                    }
                    Assert.Equal(FileSizeLimit, stream.Position);
                    Assert.Equal(FileSizeLimit, stream.Length);
                    Assert.Equal(1, memory.PinCount);
                    Assert.Equal(1, memory.UnpinCount);
                    AssertExtensions.SequenceEqual(memory.GetSpan().Slice(0, FileSizeLimit - 7),
                        File.ReadAllBytes(path).AsSpan(7));

                    await stream.WriteAsync(new byte[] { 42, 43 });
                    Assert.Equal(FileSizeLimit + 2, stream.Position);
                    AssertExtensions.SequenceEqual(new byte[] { 42, 43 }.AsSpan(),
                        File.ReadAllBytes(path).AsSpan(FileSizeLimit));
                }
                finally
                {
                    File.Delete(path);
                }
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void FileStream_PinFailureRestoresPosition()
        {
            RemoteExecutor.Invoke(async () =>
            {
                string path = Path.GetTempFileName();
                try
                {
                    using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                        FileShare.ReadWrite, bufferSize: 1, FileOptions.Asynchronous);
                    stream.Position = 7;
                    using TrackingMemoryManager memory = new TrackingMemoryManager(4096) { ThrowOnPin = true };
                    InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => stream.WriteAsync(memory.Memory).AsTask());
                    Assert.Equal("Pin failed.", error.Message);
                    Assert.Equal(7, stream.Position);
                    Assert.Equal(0, stream.Length);
                    memory.ThrowOnPin = false;
                    await stream.WriteAsync(memory.Memory);
                    Assert.Equal(7 + memory.Memory.Length, stream.Position);
                    Assert.Equal(stream.Position, stream.Length);
                    Assert.Equal(1, memory.PinCount);
                    Assert.Equal(1, memory.UnpinCount);
                }
                finally
                {
                    File.Delete(path);
                }
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void FileWriteGather_VectorLimitPreservesEmptyBuffersAndOffsets(bool useAsync)
        {
            RemoteExecutor.Invoke(async asyncText =>
            {
                string path = Path.GetTempFileName();
                try
                {
                    using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite,
                        FileShare.ReadWrite, FileOptions.Asynchronous);
                    byte[] data = new byte[1200 * 1024];
                    new Random(42).NextBytes(data);
                    ReadOnlyMemory<byte>[] buffers = new ReadOnlyMemory<byte>[2401];
                    for (int i = 0; i < 1200; i++)
                    {
                        buffers[2 * i + 1] = data.AsMemory(i * 1024, 1024);
                    }
                    if (bool.Parse(asyncText))
                    {
                        await RandomAccess.WriteAsync(handle, buffers, 7);
                    }
                    else
                    {
                        RandomAccess.Write(handle, buffers, 7);
                    }
                    byte[] actual = File.ReadAllBytes(path);
                    Assert.Equal(data.Length + 7, actual.Length);
                    AssertExtensions.SequenceEqual(new byte[7].AsSpan(), actual.AsSpan(0, 7));
                    AssertExtensions.SequenceEqual(data.AsSpan(), actual.AsSpan(7));
                }
                finally
                {
                    File.Delete(path);
                }
            }, useAsync.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void FileSubmission_PinFailureReleasesStateAndAllowsReuse(bool write, bool vector)
        {
            RemoteExecutor.Invoke(async (writeText, vectorText) =>
            {
                bool isWrite = bool.Parse(writeText);
                bool isVector = bool.Parse(vectorText);
                string path = Path.GetTempFileName();
                try
                {
                    File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                    using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.Asynchronous);
                    using TrackingMemoryManager first = new TrackingMemoryManager();
                    using TrackingMemoryManager failing = new TrackingMemoryManager { ThrowOnPin = true };
                    using TrackingMemoryManager last = new TrackingMemoryManager();
                    Memory<byte>[] reads = [first.Memory, failing.Memory, last.Memory];
                    ReadOnlyMemory<byte>[] writes = [first.Memory, failing.Memory, last.Memory];
                    first.GetSpan()[0] = 41;
                    failing.GetSpan()[0] = 42;
                    last.GetSpan()[0] = 43;

                    // Queueing must return a task, not throw; failure must not run blocking fallback I/O.
                    Task failed = QueueIo();
                    InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
                    Assert.Equal("Pin failed.", error.Message);
                    Assert.Equal(isVector ? 1 : 0, first.PinCount);
                    Assert.Equal(first.PinCount, first.UnpinCount);
                    Assert.Equal(0, failing.PinCount);
                    Assert.Equal(0, last.PinCount);
                    Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));

                    failing.ThrowOnPin = false;
                    await QueueIo();
                    Assert.Equal(first.PinCount, first.UnpinCount);
                    Assert.Equal(failing.PinCount, failing.UnpinCount);
                    Assert.Equal(last.PinCount, last.UnpinCount);
                    if (isWrite)
                    {
                        Assert.Equal(isVector ? new byte[] { 41, 42, 43 } : new byte[] { 42, 2, 3 }, File.ReadAllBytes(path));
                    }
                    else
                    {
                        Assert.Equal(isVector ? 2 : 1, failing.GetSpan()[0]);
                        if (isVector)
                        {
                            Assert.Equal(1, first.GetSpan()[0]);
                            Assert.Equal(3, last.GetSpan()[0]);
                        }
                    }
                    handle.Dispose();
                    Assert.True(handle.IsClosed);

                    Task QueueIo() => (isWrite, isVector) switch
                    {
                        (false, false) => RandomAccess.ReadAsync(handle, failing.Memory, 0).AsTask(),
                        (true, false) => RandomAccess.WriteAsync(handle, failing.Memory, 0).AsTask(),
                        (false, true) => RandomAccess.ReadAsync(handle, reads, 0).AsTask(),
                        (true, true) => RandomAccess.WriteAsync(handle, writes, 0).AsTask()
                    };
                }
                finally
                {
                    File.Delete(path);
                }
            }, write.ToString(), vector.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void Initialization_FailedBufferRegistrationClosesRing()
        {
            RemoteInvokeOptions options = CreateOptions(3);
            options.StartInfo.Environment["DOTNET_IORING_RECV_BUFFER_COUNT"] = "3";
            RemoteExecutor.Invoke(() =>
            {
                Assert.False(IoUring.IsSupported);
                foreach (string descriptor in System.IO.Directory.EnumerateFiles("/proc/self/fd"))
                {
                    Assert.NotEqual("anon_inode:[io_uring]", new System.IO.FileInfo(descriptor).LinkTarget);
                }
            }, options).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void Initialization_DoesNotFlowCallerExecutionContext()
        {
            RemoteExecutor.Invoke(() =>
            {
                int contextChanges = 0;
                AsyncLocal<object> local = new AsyncLocal<object>(change =>
                {
                    if (change.ThreadContextChanged && change.CurrentValue is not null)
                    {
                        Interlocked.Increment(ref contextChanges);
                    }
                });
                local.Value = new object();
                try
                {
                    Assert.True(IoUring.IsSupported);
                    Assert.Equal(0, Volatile.Read(ref contextChanges));
                }
                finally
                {
                    local.Value = null;
                }
            }, CreateOptions(3)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void DatagramReceive_PreservesTruncationFlag(bool pending)
        {
            RemoteExecutor.Invoke(async pendingText =>
            {
                using Socket receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                using Socket sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                using SocketAsyncEventArgs args = new SocketAsyncEventArgs();
                args.SetBuffer(new byte[1]);
                TaskCompletionSource completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                args.Completed += (_, _) => completion.SetResult();
                bool shouldPend = bool.Parse(pendingText);
                if (!shouldPend)
                {
                    Assert.Equal(2, sender.SendTo(new byte[] { 42, 43 }, receiver.LocalEndPoint!));
                }
                bool isPending = receiver.ReceiveAsync(args);
                Assert.Equal(shouldPend, isPending);
                if (shouldPend)
                {
                    Assert.Equal(2, sender.SendTo(new byte[] { 42, 43 }, receiver.LocalEndPoint!));
                    await completion.Task.WaitAsync(TestSettings.PassingTestTimeout);
                }
                Assert.Equal(SocketError.Success, args.SocketError);
                Assert.Equal(1, args.BytesTransferred);
                Assert.Equal(42, args.MemoryBuffer.Span[0]);
                Assert.Equal(SocketFlags.Truncated, args.SocketFlags & SocketFlags.Truncated);
            }, pending.ToString(), CreateOptions(1)).Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SendVector
        {
            public IntPtr Base;
            public UIntPtr Count;
        }

        private sealed class CallbackOperation : IoUringOperation
        {
            private readonly IoUringRequest _request;
            private readonly Action<int> _callback;
            private int _result;

            public CallbackOperation(IoUringRequest request, Action<int> callback)
            {
                _request = request;
                _callback = callback;
            }

            protected override IoUringRequest PrepareRequest() => _request;

            protected override IoUringCompletionAction ProcessCompletion(in IoUringCompletion completion)
            {
                _result = completion.Result;
                return IoUringCompletionAction.Complete;
            }

            protected override void OnCompleted(Exception? error)
            {
                Assert.Null(error);
                _callback(_result);
            }
        }

        private sealed class CompletionActionOperation : IoUringOperation
        {
            private readonly IoUringRequest _request;
            private readonly Func<IoUringCompletion, IoUringCompletionAction> _process;
            private readonly Action<Exception?> _completed;
            private readonly Action? _completing;
            private readonly Action<IoUringCompletion>? _discarded;

            public CompletionActionOperation(IoUringRequest request,
                Func<IoUringCompletion, IoUringCompletionAction> process, Action<Exception?> completed,
                Action? completing = null, Action<IoUringCompletion>? discarded = null)
            {
                _request = request;
                _process = process;
                _completed = completed;
                _completing = completing;
                _discarded = discarded;
            }

            protected override IoUringRequest PrepareRequest() => _request;
            protected override IoUringCompletionAction ProcessCompletion(in IoUringCompletion completion) => _process(completion);
            protected override void OnCompleted(Exception? error) => _completed(error);
            protected override void OnCompleting() => _completing?.Invoke();
            protected override void OnCompletionDiscarded(in IoUringCompletion completion) => _discarded?.Invoke(completion);

            public IMemoryOwner<byte>? TakeCurrentBuffer() => TakeBuffer();
        }

        private static IoUringOperation EnqueueMultishot(IoRingBoundHandle binding, Action<int, IMemoryOwner<byte>?, bool> callback)
        {
            IoUringOperation operation = new TestReceiveOperation(callback);
            binding.EnqueueForSubmission(operation);
            return operation;
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
                using (CountingHandle owner = new CountingHandle(receiver.SafeHandle.DangerousGetHandle(), new ReleaseCounter()))
                {
                    IoRingBoundHandle binding = owner.Binding;
                    Assert.Same(binding, IoUring.Bind(owner, ownsFileDescriptor: true));
                    BoundReceiveOperation operation = new BoundReceiveOperation();
                    for (int i = 0; i < 50; i++)
                    {
                        using CancellationTokenSource cancellation = new CancellationTokenSource();
                        operation.Prepare();
                        binding.EnqueueForSubmission(operation, bool.Parse(tokenText) ? cancellation.Token : default);
                        Assert.Throws<InvalidOperationException>(() => binding.EnqueueForSubmission(operation));
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
                        binding.EnqueueForSubmission(operation);
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
                using (CountingHandle owner = new CountingHandle(receiver.SafeHandle.DangerousGetHandle(), new ReleaseCounter()))
                {
                    IoRingBoundHandle binding = owner.Binding;
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
                        binding.EnqueueForSubmission(operations[i]);
                    }
                    switch (int.Parse(kindText))
                    {
                        case 0:
                            binding.Dispose();
                            break;
                        case 1:
                            owner.Close();
                            break;
                        case 2:
                            owner.Dispose();
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
                    Assert.Throws<ObjectDisposedException>(() => binding.EnqueueForSubmission(new BoundReceiveOperation()));
                    if (int.Parse(kindText) == 0)
                    {
                        sender.Send(new byte[] { 7 });
                        Assert.Equal(1, receiver.Receive(new byte[1]));
                    }
                }
            }, closeKind.ToString(), count.ToString(), exhaustGenerations.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void Receive_RacingSocketDispose_DoesNotRetainHandle(bool disposeHandle)
        {
            RemoteExecutor.Invoke(async disposeHandleText =>
            {
                for (int i = 0; i < 100; i++)
                {
                    (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                    using (sender)
                    using (receiver)
                    {
                        SafeSocketHandle handle = receiver.SafeHandle;
                        using Barrier barrier = new Barrier(2);
                        Task receive = Task.Run(async () =>
                        {
                            barrier.SignalAndWait();
                            try
                            {
                                await receiver.ReceiveAsync(new byte[1], SocketFlags.None);
                            }
                            catch (ObjectDisposedException)
                            {
                            }
                            // Close can abort the optimistic syscall before the receive is queued.
                            catch (SocketException error) when (error.SocketErrorCode is SocketError.OperationAborted or SocketError.ConnectionReset)
                            {
                            }
                        });
                        Task close = Task.Run(() =>
                        {
                            barrier.SignalAndWait();
                            if (bool.Parse(disposeHandleText))
                            {
                                handle.Dispose();
                            }
                            else
                            {
                                receiver.Dispose();
                            }
                        });
                        await Task.WhenAll(receive, close).WaitAsync(TestSettings.PassingTestTimeout);
                        if (bool.Parse(disposeHandleText))
                        {
                            Assert.True(SpinWait.SpinUntil(() => handle.IsClosed, TestSettings.PassingTestTimeout));
                        }
                        else
                        {
                            Assert.True(handle.IsClosed);
                        }
                    }
                }
            }, disposeHandle.ToString(), CreateOptions(3)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void BoundOperation_CancellationAndReuseAcrossRings()
        {
            RemoteExecutor.Invoke(async () =>
            {
                List<(Socket Sender, Socket Receiver, CountingHandle Owner)> pairs = new();
                try
                {
                    HashSet<long> rings = new HashSet<long>();
                    for (int i = 0; i < 3; i++)
                    {
                        (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                        pairs.Add((sender, receiver, new CountingHandle(receiver.SafeHandle.DangerousGetHandle(), new ReleaseCounter())));
                        rings.Add(receiver.SafeHandle.DangerousGetHandle().ToInt64() % 3);
                    }
                    Assert.True(rings.Count > 1);
                    BoundReceiveOperation operation = new BoundReceiveOperation();
                    for (int iteration = 0; iteration < 30; iteration++)
                    {
                        foreach ((Socket sender, Socket receiver, CountingHandle owner) in pairs)
                        {
                            IoRingBoundHandle binding = owner.Binding;
                            using CancellationTokenSource cancellation = new CancellationTokenSource();
                            operation.Prepare();
                            binding.EnqueueForSubmission(operation, cancellation.Token);
                            cancellation.Cancel();
                            Assert.Equal(-125, await operation.Completion.WaitAsync(TestSettings.PassingTestTimeout));
                            operation.Prepare();
                            binding.EnqueueForSubmission(operation);
                            sender.Send(new byte[] { 42 });
                            Assert.Equal(1, await operation.Completion.WaitAsync(TestSettings.PassingTestTimeout));
                            Assert.Equal(42, operation.Value);
                        }
                    }
                }
                finally
                {
                    foreach ((Socket sender, Socket receiver, CountingHandle owner) in pairs)
                    {
                        owner.Dispose();
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
                Assert.Throws<ObjectDisposedException>(() => IoUring.Bind(handle, ownsFileDescriptor: true));
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
                IoRingBoundHandle binding = handle.Binding;
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
            IoRingBoundHandle binding = handle.Binding;
            return (new WeakReference(handle), new WeakReference(binding));
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Bind_DistinctWrappersShareBindingAndCancellation(bool bindBorrowedFirst, bool socketWrapper)
        {
            RemoteExecutor.Invoke(async (borrowedText, socketText) =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (SafeHandle borrowed = bool.Parse(socketText)
                    ? new SafeSocketHandle(receiver.Handle, ownsHandle: false)
                    : new SafeFileHandle(receiver.Handle, ownsHandle: false))
                {
                    bool borrowedFirst = bool.Parse(borrowedText);
                    using IoRingBoundHandle first = IoUring.Bind(borrowedFirst ? borrowed : receiver.SafeHandle, ownsFileDescriptor: !borrowedFirst);
                    using IoRingBoundHandle second = IoUring.Bind(borrowedFirst ? receiver.SafeHandle : borrowed, ownsFileDescriptor: borrowedFirst);
                    Assert.Same(first, second);
                    IoRingBoundHandle[] simultaneous = await Task.WhenAll(
                        Task.Run(() => IoUring.Bind(borrowed, ownsFileDescriptor: false)),
                        Task.Run(() => IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true)));
                    Assert.All(simultaneous, binding => Assert.Same(first, binding));

                    BoundReceiveOperation read = new();
                    read.Prepare();
                    first.EnqueueForSubmission(read);
                    second.DisposeAndWait();
                    Assert.Equal(-125, await read.Completion.WaitAsync(TestSettings.PassingTestTimeout));
                    Assert.Throws<ObjectDisposedException>(() => first.EnqueueForSubmission(read));
                    Assert.False(receiver.SafeHandle.IsClosed);
                    Assert.Equal(1, sender.Send(new byte[] { 17 }));
                    Assert.Equal(1, receiver.Receive(new byte[1]));

                    using IoRingBoundHandle rebound = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true);
                    Assert.NotSame(first, rebound);
                    Assert.Same(rebound, IoUring.Bind(borrowed, ownsFileDescriptor: false));
                }
            }, bindBorrowedFirst.ToString(), socketWrapper.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Bind_OwnerPromotionReleasesBorrowedReferences(bool bindBorrowedFirst, bool socketWrapper)
        {
            RemoteExecutor.Invoke(async (borrowedText, socketText) =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (SafeHandle borrowed = bool.Parse(socketText)
                    ? new SafeSocketHandle(receiver.Handle, ownsHandle: false)
                    : new SafeFileHandle(receiver.Handle, ownsHandle: false))
                using (SafeHandle alias = bool.Parse(socketText)
                    ? new SafeSocketHandle(receiver.Handle, ownsHandle: false)
                    : new SafeFileHandle(receiver.Handle, ownsHandle: false))
                {
                    bool borrowedFirst = bool.Parse(borrowedText);
                    SafeSocketHandle owner = receiver.SafeHandle;
                    using IoRingBoundHandle binding = IoUring.Bind(borrowedFirst ? borrowed : owner, ownsFileDescriptor: !borrowedFirst);
                    Assert.Same(binding, IoUring.Bind(alias, ownsFileDescriptor: false));
                    BoundReceiveOperation operation = new BoundReceiveOperation();
                    operation.Prepare();
                    binding.EnqueueForSubmission(operation);

                    Assert.Same(binding, IoUring.Bind(borrowedFirst ? owner : borrowed, ownsFileDescriptor: borrowedFirst));
                    Assert.Same(binding, IoUring.Bind(alias, ownsFileDescriptor: false));
                    borrowed.Dispose();
                    alias.Dispose();
                    Assert.True(borrowed.IsClosed);
                    Assert.True(alias.IsClosed);
                    Assert.False(operation.Completion.IsCompleted);

                    owner.Dispose();
                    Assert.False(owner.IsClosed);
                    binding.DisposeAndWait();
                    Assert.True(owner.IsClosed);
                    Assert.Equal(-125, await operation.Completion.WaitAsync(TestSettings.PassingTestTimeout));
                }
            }, bindBorrowedFirst.ToString(), socketWrapper.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void Bind_ConcurrentOwners_OnlyOneIsRetained()
        {
            RemoteExecutor.Invoke(async () =>
            {
                using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using SafeFileHandle borrowed = new SafeFileHandle(socket.Handle, ownsHandle: false);
                ReleaseCounter firstCounter = new ReleaseCounter();
                ReleaseCounter secondCounter = new ReleaseCounter();
                using CountingHandle first = new CountingHandle(socket.Handle, firstCounter);
                using CountingHandle second = new CountingHandle(socket.Handle, secondCounter);
                using IoRingBoundHandle binding = IoUring.Bind(borrowed, ownsFileDescriptor: false);
                using Barrier barrier = new Barrier(2);
                Exception?[] errors = await Task.WhenAll(
                    Task.Run(() =>
                    {
                        barrier.SignalAndWait();
                        return Record.Exception(() => Assert.Same(binding, IoUring.Bind(first, ownsFileDescriptor: true)));
                    }),
                    Task.Run(() =>
                    {
                        barrier.SignalAndWait();
                        return Record.Exception(() => Assert.Same(binding, IoUring.Bind(second, ownsFileDescriptor: true)));
                    }));
                Assert.Single(errors, error => error is null);
                Assert.Single(errors, error => error is ArgumentException argument && argument.ParamName == "handle");

                borrowed.Dispose();
                Assert.True(borrowed.IsClosed);
                first.Dispose();
                second.Dispose();
                Assert.Equal(1, firstCounter.Count + secondCounter.Count);
                binding.DisposeAndWait();
                Assert.Equal(1, firstCounter.Count);
                Assert.Equal(1, secondCounter.Count);
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void Bind_OwnerPromotionRacingMultishotPreparation(bool socketWrapper)
        {
            RemoteExecutor.Invoke(async socketText =>
            {
                for (int i = 0; i < 30; i++)
                {
                    (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                    using (sender)
                    using (receiver)
                    using (SafeHandle borrowed = bool.Parse(socketText)
                        ? new SafeSocketHandle(receiver.Handle, ownsHandle: false)
                        : new SafeFileHandle(receiver.Handle, ownsHandle: false))
                    using (IoRingBoundHandle binding = IoUring.Bind(borrowed, ownsFileDescriptor: false))
                    using (Barrier barrier = new Barrier(2))
                    {
                        borrowed.Dispose();
                        TaskCompletionSource<int> completion = new TaskCompletionSource<int>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        await Task.WhenAll(
                            Task.Run(() =>
                            {
                                barrier.SignalAndWait();
                                Assert.Same(binding, IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true));
                            }),
                            Task.Run(() =>
                            {
                                barrier.SignalAndWait();
                                EnqueueMultishot(binding, (result, buffer, more) =>
                                {
                                    buffer?.Dispose();
                                    if (!more)
                                    {
                                        completion.SetResult(result);
                                    }
                                });
                            })).WaitAsync(TestSettings.PassingTestTimeout);
                        Assert.True(borrowed.IsClosed);
                        binding.DisposeAndWait();
                        Assert.Equal(-125, await completion.Task.WaitAsync(TestSettings.PassingTestTimeout));
                    }
                }
            }, socketWrapper.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void BorrowedSocket_AsyncReceivePreservesOwner(bool bindOwnerFirst)
        {
            RemoteExecutor.Invoke(async ownerText =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (SafeSocketHandle borrowedHandle = new SafeSocketHandle(receiver.Handle, ownsHandle: false))
                using (Socket borrowed = new Socket(borrowedHandle))
                {
                    bool ownerFirst = bool.Parse(ownerText);
                    using IoRingBoundHandle? initialBinding = ownerFirst
                        ? IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true) : null;
                    byte[] data = new byte[1];
                    Task<int> receive = borrowed.ReceiveAsync(data.AsMemory(), SocketFlags.None).AsTask();
                    Assert.False(receive.IsCompleted);
                    using IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true);
                    Assert.Same(binding, IoUring.Bind(borrowedHandle, ownsFileDescriptor: false));
                    Assert.Equal(1, sender.Send(new byte[] { 42 }));
                    Assert.Equal(1, await receive.WaitAsync(TestSettings.PassingTestTimeout));
                    Assert.Equal(42, data[0]);
                    borrowed.Dispose();
                    Assert.False(receiver.SafeHandle.IsClosed);
                    Assert.Equal(1, sender.Send(new byte[] { 43 }));
                    Assert.Equal(1, receiver.Receive(data));
                    Assert.Equal(43, data[0]);
                }
            }, bindOwnerFirst.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void BorrowedFile_AsyncReadPreservesOwner(bool bindOwnerFirst)
        {
            RemoteExecutor.Invoke(async ownerText =>
            {
                string path = Path.GetTempFileName();
                try
                {
                    File.WriteAllBytes(path, new byte[] { 42 });
                    using SafeFileHandle owner = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.Asynchronous);
                    using SafeFileHandle borrowed = new SafeFileHandle(owner.DangerousGetHandle(), ownsHandle: false);
                    using IoRingBoundHandle? initialBinding = bool.Parse(ownerText)
                        ? IoUring.Bind(owner, ownsFileDescriptor: true) : null;
                    byte[] data = new byte[1];
                    Assert.Equal(1, await RandomAccess.ReadAsync(borrowed, data, 0));
                    Assert.Equal(42, data[0]);
                    using IoRingBoundHandle binding = IoUring.Bind(owner, ownsFileDescriptor: true);
                    Assert.Same(binding, IoUring.Bind(borrowed, ownsFileDescriptor: false));
                    borrowed.Dispose();
                    Assert.False(owner.IsClosed);
                    Assert.Equal(1, RandomAccess.Read(owner, data, 0));
                    Assert.Equal(42, data[0]);
                }
                finally
                {
                    File.Delete(path);
                }
            }, bindOwnerFirst.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void Bind_OwnerReferenceIsBalancedAndReleasedOutsideRegistryLock()
        {
            RemoteExecutor.Invoke(async () =>
            {
                using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                ReleaseCounter firstCounter = new();
                ReleaseCounter secondCounter = new();
                using CountingHandle first = new(socket.Handle, firstCounter);
                using CountingHandle second = new(socket.Handle, secondCounter);
                using IoRingBoundHandle binding = IoUring.Bind(first, ownsFileDescriptor: true);
                Assert.Throws<ArgumentException>("handle", () => IoUring.Bind(second, ownsFileDescriptor: true));
                for (int i = 0; i < 20; i++)
                {
                    Assert.Same(binding, IoUring.Bind(first, ownsFileDescriptor: true));
                }

                // These wrappers deliberately leave binding disposal to the explicit owner below.
                first.Dispose();
                second.Dispose();
                Assert.Equal(0, firstCounter.Count);
                Assert.Equal(1, secondCounter.Count);
                firstCounter.OnRelease = () =>
                {
                    binding.DisposeAndWait();
                    Task.Run(() =>
                    {
                        using IoRingBoundHandle replacement = IoUring.Bind(socket.SafeHandle, ownsFileDescriptor: true);
                        Assert.NotSame(binding, replacement);
                    }).WaitAsync(TestSettings.PassingTestTimeout).GetAwaiter().GetResult();
                };
                await Task.Run(() => binding.DisposeAndWait()).WaitAsync(TestSettings.PassingTestTimeout);
                Assert.Equal(1, firstCounter.Count);
                Assert.Equal(1, secondCounter.Count);
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void Receive_PinThrows_ReleasesQueueReservation()
        {
            RemoteExecutor.Invoke(async () =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (TrackingMemoryManager memory = new())
                {
                    memory.ThrowOnPin = true;
                    await Assert.ThrowsAsync<InvalidOperationException>(
                        () => receiver.ReceiveAsync(memory.Memory, SocketFlags.None).AsTask());
                    memory.ThrowOnPin = false;
                    Task<int> pending = receiver.ReceiveAsync(memory.Memory, SocketFlags.None).AsTask();
                    Assert.False(pending.IsCompleted);
                    Assert.Equal(1, sender.Send(new byte[] { 23 }));
                    Assert.Equal(1, await pending.WaitAsync(TestSettings.PassingTestTimeout));
                    Assert.Equal(23, memory.GetSpan()[0]);
                    Assert.Equal(1, memory.PinCount);
                    Assert.Equal(1, memory.UnpinCount);
                }
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void Bind_DescriptorReplacedDuringRelease_PreservesNewBinding()
        {
            RemoteExecutor.Invoke(() =>
            {
                (Socket sender, Socket source) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (source)
                using (Socket original = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    IntPtr descriptor = original.Handle;
                    ReleaseCounter counter = new();
                    using CountingHandle owner = new(descriptor, counter);
                    using IoRingBoundHandle oldBinding = IoUring.Bind(owner, ownsFileDescriptor: true);
                    using Microsoft.Win32.SafeHandles.SafeFileHandle alias = new(descriptor, ownsHandle: false);
                    Assert.Same(oldBinding, IoUring.Bind(alias, ownsFileDescriptor: false));
                    Socket? replacement = null;
                    IoRingBoundHandle? newBinding = null;
                    counter.OnRelease = () =>
                    {
                        // dup2 atomically closes the old descriptor and installs a different socket
                        // at that number, without a free-fd window for an unrelated allocation.
                        Assert.Equal(descriptor.ToInt32(), DuplicateDescriptor(source.Handle.ToInt32(), descriptor.ToInt32()));
                        original.SafeHandle.SetHandleAsInvalid();
                        replacement = new Socket(new SafeSocketHandle(descriptor, ownsHandle: true));
                        newBinding = IoUring.Bind(replacement.SafeHandle, ownsFileDescriptor: true);
                        Assert.NotSame(oldBinding, newBinding);
                    };
                    owner.Dispose();
                    try
                    {
                        oldBinding.DisposeAndWait();
                        Assert.NotNull(replacement);
                        Assert.Same(newBinding, IoUring.Bind(replacement.SafeHandle, ownsFileDescriptor: true));
                        Assert.Equal(1, sender.Send(new byte[] { 31 }));
                        byte[] buffer = new byte[1];
                        Assert.Equal(1, replacement.Receive(buffer));
                        Assert.Equal(31, buffer[0]);
                        Assert.Equal(1, counter.Count);
                    }
                    finally
                    {
                        newBinding?.DisposeAndWait();
                        replacement?.Dispose();
                    }
                }
            }, CreateOptions(1)).Dispose();
        }

        [DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
        private static extern int DuplicateDescriptor(int source, int destination);

        private sealed class ReleaseCounter
        {
            public int Count;
            public Action? OnRelease;
        }

        [ConditionalFact(nameof(IsSupported))]
        public void Bind_FinalizerPendingBinding_RemainsCanonical()
        {
            RemoteExecutor.Invoke(() =>
            {
                using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using ManualResetEventSlim entered = new();
                using ManualResetEventSlim release = new();
                BlockFinalizer(entered, release);
                ReleaseCounter counter = new();
                try
                {
                    GC.Collect();
                    Assert.True(entered.Wait(TestSettings.PassingTestTimeout));
                    WeakReference<IoRingBoundHandle> weak = CreateFinalizableBinding(socket.Handle, counter);
                    GC.Collect();
                    Assert.True(weak.TryGetTarget(out IoRingBoundHandle? original));
                    using Microsoft.Win32.SafeHandles.SafeFileHandle alias = new(socket.Handle, ownsHandle: false);
                    using IoRingBoundHandle binding = IoUring.Bind(alias, ownsFileDescriptor: false);
                    Assert.Same(original, binding);
                    binding.DisposeAndWait();
                }
                finally
                {
                    release.Set();
                    GC.WaitForPendingFinalizers();
                }
                Assert.Equal(1, counter.Count);
            }, CreateOptions(1)).Dispose();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference<IoRingBoundHandle> CreateFinalizableBinding(IntPtr descriptor, ReleaseCounter counter)
        {
            CountingHandle handle = new(descriptor, counter);
            return new WeakReference<IoRingBoundHandle>(handle.Binding, trackResurrection: true);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void BlockFinalizer(ManualResetEventSlim entered, ManualResetEventSlim release) =>
            _ = new FinalizerGate(entered, release);

        private sealed class FinalizerGate
        {
            private readonly ManualResetEventSlim _entered;
            private readonly ManualResetEventSlim _release;

            public FinalizerGate(ManualResetEventSlim entered, ManualResetEventSlim release)
            {
                _entered = entered;
                _release = release;
            }

            ~FinalizerGate()
            {
                _entered.Set();
                _release.Wait();
            }
        }

        private sealed class CountingHandle : SafeHandle
        {
            private readonly ReleaseCounter _counter;
            private bool _disposeDuringValidation;
            private readonly object _gate = new();
            private IoRingBoundHandle? _binding;
            private bool _disposed;

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

            public IoRingBoundHandle Binding
            {
                get
                {
                    lock (_gate)
                    {
                        ObjectDisposedException.ThrowIf(_disposed, this);
                        return _binding ??= IoUring.Bind(this, ownsFileDescriptor: true);
                    }
                }
            }

            protected override void Dispose(bool disposing)
            {
                lock (_gate)
                {
                    _disposed = true;
                    _binding?.DisposeAndWait();
                    base.Dispose(disposing);
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
                using (IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true))
                {
                    BoundReceiveOperation operation = new BoundReceiveOperation();
                    operation.Prepare();
                    await Task.Run(() =>
                    {
                        binding.EnqueueForSubmission(operation);
                        binding.DisposeAndWait();
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
            private int _result;

            public Task<int> Completion => _completion.Task;
            public byte Value => _buffer[0];

            public void Prepare() =>
                _completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override unsafe IoUringRequest PrepareRequest()
            {
                fixed (byte* buffer = _buffer)
                {
                    return new IoUringRequest(IoUringOperationKind.Receive, buffer, _buffer.Length);
                }
            }

            protected override IoUringCompletionAction ProcessCompletion(in IoUringCompletion completion)
            {
                _result = completion.Result;
                return IoUringCompletionAction.Complete;
            }

            protected override void OnCompleted(Exception? error)
            {
                TaskCompletionSource<int> completion = _completion;
                Assert.Null(error);
                completion.SetResult(_result);
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
                using (IoRingBoundHandle binding = IoUring.Bind(sender.SafeHandle, ownsFileDescriptor: true))
                using (ManualResetEventSlim delivered = new ManualResetEventSlim())
                using (ManualResetEventSlim resume = new ManualResetEventSlim())
                {
                    PausedSendOperation operation = new PausedSendOperation(delivered, resume);
                    binding.EnqueueForSubmission(operation);
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
            private bool _succeeded;

            public PausedSendOperation(ManualResetEventSlim delivered, ManualResetEventSlim resume)
            {
                _delivered = delivered;
                _resume = resume;
            }

            public Task<bool> Completion => _completion.Task;

            protected override unsafe IoUringRequest PrepareRequest()
            {
                fixed (byte* buffer = _buffer)
                {
                    return new IoUringRequest(IoUringOperationKind.Send, buffer, _buffer.Length);
                }
            }

            protected override IoUringCompletionAction ProcessCompletion(in IoUringCompletion completion)
            {
                _delivered.Set();
                bool resumed = _resume.Wait(TestSettings.PassingTestTimeout);
                bool canceled = IsCancellationRequested;
                _succeeded = resumed && canceled && completion.Result == 1;
                return IoUringCompletionAction.Complete;
            }

            protected override void OnCompleted(Exception? error)
            {
                Assert.Null(error);
                _completion.SetResult(_succeeded);
            }
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false, 2)]
        [InlineData(true, 2)]
        [InlineData(true, 1200)]
        public void Send_WaitAll_CompletesOnceAtNativeVectorLimit(bool gather, int vectorCount)
        {
            RemoteExecutor.Invoke(async (gatherText, countText) =>
            {
                bool useGather = bool.Parse(gatherText);
                int count = int.Parse(countText);
                int bufferSize = count == 2 ? 1024 * 1024 : 2048;
                byte[] data = new byte[count * bufferSize];
                new Random(42).NextBytes(data);
                SendVector[] vectors = new SendVector[count];
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (IoRingBoundHandle binding = IoUring.Bind(sender.SafeHandle, ownsFileDescriptor: true))
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
                            binding.EnqueueForSubmission(new CallbackOperation(
                                useGather
                                    ? new IoUringRequest(IoUringOperationKind.SendGather, (void*)vectorsPin.AddrOfPinnedObject(), count)
                                    : new IoUringRequest(IoUringOperationKind.Send, (void*)dataPin.AddrOfPinnedObject(), data.Length), result =>
                            {
                                Interlocked.Increment(ref callbacks);
                                completion.TrySetResult(result);
                            }));
                        }

                        int expected = useGather ? Math.Min(count, 1024) * bufferSize : data.Length;
                        byte[] received = new byte[expected];
                        using NetworkStream stream = new NetworkStream(receiver, ownsSocket: false);
                        ValueTask readTask = stream.ReadExactlyAsync(received);
                        Assert.Equal(expected, await completion.Task.WaitAsync(TestSettings.PassingTestTimeout));
                        await readTask.AsTask().WaitAsync(TestSettings.PassingTestTimeout);
                        Assert.Equal(1, Volatile.Read(ref callbacks));
                        AssertExtensions.SequenceEqual(data.AsSpan(0, expected), received.AsSpan());
                    }
                    finally
                    {
                        // Drain kernel ownership before unpinning, including on assertion failures.
                        binding.DisposeAndWait();
                        sender.Dispose();
                        if (vectorsPin.IsAllocated)
                        {
                            vectorsPin.Free();
                        }
                        dataPin.Free();
                    }
                }
            }, gather.ToString(), vectorCount.ToString(), CreateOptions(1)).Dispose();
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
        public void SocketAsyncEngine_PeekUsesSelectedReadinessBackend(bool useIoUring)
        {
            RemoteInvokeOptions options = CreateOptions(1);
            options.StartInfo.Environment["DOTNET_USE_IO_URING"] = useIoUring ? "1" : "0";
            options.StartInfo.Environment["DOTNET_SYSTEM_NET_SOCKETS_THREAD_COUNT"] = "1";
            RemoteExecutor.Invoke(async enabledText =>
            {
                bool enabled = bool.Parse(enabledText);
                Assert.Equal(enabled, IoUring.IsSupported);
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                {
                    byte[] buffer = new byte[1];
                    Task<int> pending = receiver.ReceiveAsync(buffer.AsMemory(), SocketFlags.Peek).AsTask();
                    Assert.False(pending.IsCompleted);
                    Assert.Equal(1, sender.Send(new byte[] { 42 }));
                    Assert.Equal(1, await pending.WaitAsync(TestSettings.PassingTestTimeout));
                    Assert.Equal(42, buffer[0]);
                    Assert.Equal(1, receiver.Receive(buffer));
                    Assert.Equal(42, buffer[0]);

                    Type engineType = typeof(Socket).Assembly.GetType("System.Net.Sockets.SocketAsyncEngine", throwOnError: true)!;
#pragma warning disable IL2075 // RemoteExecutor runs this implementation-specific test without trimming.
                    Array engines = (Array)engineType.GetField("s_engines",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
#pragma warning restore IL2075
                    Assert.Equal(enabled ? 0 : 1, engines.Length);
                }
            }, useIoUring.ToString(), options).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void SynchronousReceive_DisposeUnblocksPoll(bool closeHandle)
        {
            RemoteExecutor.Invoke(closeText =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                {
                    receiver.Blocking = false;
                    receiver.Blocking = true;
#pragma warning disable IL2075 // The RemoteExecutor process is untrimmed.
                    System.Reflection.FieldInfo stateField = typeof(SafeHandle).GetField("_state",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
#pragma warning restore IL2075
                    int initialState = (int)stateField.GetValue(receiver.SafeHandle)!;
                    SocketError error = SocketError.Success;
                    Thread reader = new Thread(() =>
                    {
                        try
                        {
                            receiver.Receive(new byte[1]);
                        }
                        catch (SocketException exception)
                        {
                            error = exception.SocketErrorCode;
                        }
                        catch (ObjectDisposedException)
                        {
                            error = SocketError.OperationAborted;
                        }
                    });
                    reader.IsBackground = true;
                    reader.Start();
                    try
                    {
                        // Wait until the blocking poll has acquired its SafeHandle reference.
                        Assert.True(SpinWait.SpinUntil(
                            () => (int)stateField.GetValue(receiver.SafeHandle)! > initialState,
                            TestSettings.PassingTestTimeout));
                        if (bool.Parse(closeText))
                        {
                            receiver.SafeHandle.Dispose();
                        }
                        else
                        {
                            receiver.Dispose();
                        }

                        Assert.True(reader.Join(TestSettings.PassingTestTimeout));
                        Assert.Contains(error, new[] { SocketError.OperationAborted, SocketError.ConnectionAborted });
                    }
                    finally
                    {
                        sender.Dispose();
                        reader.Join(TestSettings.PassingTestTimeout);
                    }
                }
            }, closeHandle.ToString(), CreateOptions(1)).Dispose();
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
                using IoRingBoundHandle binding = IoUring.Bind(listener.SafeHandle, ownsFileDescriptor: true);

                TaskCompletionSource<int> completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                int callerThreadId = Environment.CurrentManagedThreadId;
                unsafe
                {
                    binding.EnqueueForSubmission(new CallbackOperation(new IoUringRequest(IoUringOperationKind.Accept, null, 0), result =>
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
                (Socket Sender, Socket Receiver) pair = SocketTestExtensions.CreateConnectedSocketPair();
                using Socket sender = pair.Sender;
                using Socket receiver = pair.Receiver;
                using IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true);
                TaskCompletionSource<Exception?> drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                AsyncLocal<int> context = new AsyncLocal<int>();
                TrackingMemoryManager owner = new TrackingMemoryManager();
                int callbacks = 0;
                IoUringRequest request;
                unsafe
                {
                    request = TestPollOperation.CreateRequest(IoUringPollEvents.Readable);
                }
                CompletionActionOperation? operation = null;
                operation = new CompletionActionOperation(request, completion =>
                {
                    Assert.Equal(1, completion.Result);
                    Assert.False(completion.HasMore);
                    Assert.Equal(0, context.Value);
                    Assert.Null(SynchronizationContext.Current);
                    Assert.Equal(1, ++callbacks);
                    ((IDisposable)owner).Dispose();
                    context.Value = 42;
                    SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                    if (close)
                    {
                        binding.DisposeAndWait();
                        receiver.Dispose();
                    }
                    else
                    {
                        operation!.RequestCancellation();
                    }
                    return IoUringCompletionAction.Resubmit(request);
                }, error =>
                {
                    Assert.Equal(0, context.Value);
                    Assert.Null(SynchronizationContext.Current);
                    Assert.Equal(2, ++callbacks);
                    drained.SetResult(error);
                });

                // Model terminal data after native retirement, then exercise the runtime's actual
                // continuation decision, logical cleanup, and final callback context restoration.
#pragma warning disable IL2075 // The RemoteExecutor process is untrimmed.
                const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(IoUringOperation).GetMethod("Begin", Flags)!.Invoke(operation,
                    new object[] { binding, CancellationToken.None });
                typeof(IoUringOperation).GetMethod("PrepareResources", Flags)!.Invoke(operation, new object[] { request });
                System.Reflection.MethodInfo process = typeof(IoUringOperation).GetMethod("Process", Flags)!;
                object terminal = Activator.CreateInstance(typeof(IoUringCompletion), Flags, null,
                    new object[] { 1, 0u, 0UL, 0UL }, null)!;
#pragma warning restore IL2075
                await Task.Run(() => process.Invoke(operation, new[] { terminal }))
                    .WaitAsync(TestSettings.PassingTestTimeout);
                Assert.IsType<OperationCanceledException>(await drained.Task.WaitAsync(TestSettings.PassingTestTimeout));
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
                (Socket Sender, Socket Receiver) pair = SocketTestExtensions.CreateConnectedSocketPair();
                using Socket sender = pair.Sender;
                using Socket receiver = pair.Receiver;
                receiver.Blocking = false;
                sender.SendTimeout = TestSettings.PassingTestTimeout;
                using IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true);

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
                            binding.EnqueueForSubmission(new CallbackOperation(new IoUringRequest(IoUringOperationKind.Receive, pointer + i, 1), callback));
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
                (Socket Sender, Socket Receiver) pair = SocketTestExtensions.CreateConnectedSocketPair();
                using Socket sender = pair.Sender;
                using Socket receiver = pair.Receiver;
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
                (Socket Sender, Socket Receiver) pair = SocketTestExtensions.CreateConnectedSocketPair();
                using Socket sender = pair.Sender;
                using Socket receiver = pair.Receiver;
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
        [InlineData(false)]
        [InlineData(true)]
        public void BufferOperation_ReusedForBothDirections(bool reenterFromUnpin)
        {
            RemoteExecutor.Invoke(async reenterText =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (TrackingMemoryManager receiveMemory = new TrackingMemoryManager())
                using (TrackingMemoryManager sendMemory = new TrackingMemoryManager())
                {
                    bool reenter = bool.Parse(reenterText);
#pragma warning disable IL2075 // RemoteExecutor runs these implementation-specific checks without trimming.
                    const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    object context = typeof(SafeSocketHandle).GetProperty("AsyncContext", Flags)!.GetValue(receiver.SafeHandle)!;
                    Type contextType = context.GetType();
                    System.Reflection.FieldInfo operationField = contextType.GetField("_bufferOperation", Flags)!;
                    Assert.NotNull(operationField);
                    System.Reflection.MethodInfo sendMethod = contextType.GetMethod("TrySendViaIoUring", Flags, null,
                        new[] { typeof(Memory<byte>), typeof(int), typeof(int), typeof(SocketFlags), typeof(int),
                            typeof(Action<int, Memory<byte>, SocketFlags, SocketError>), typeof(CancellationToken) }, null)!;
#pragma warning restore IL2075
                    Task<int> receive = receiver.ReceiveAsync(receiveMemory.Memory, SocketFlags.None).AsTask();
                    Assert.False(receive.IsCompleted);
                    TaskCompletionSource<int> sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    Action<int, Memory<byte>, SocketFlags, SocketError> callback = (count, _, _, error) =>
                    {
                        if (error == SocketError.Success)
                        {
                            sent.SetResult(count);
                        }
                        else
                        {
                            sent.SetException(new SocketException((int)error));
                        }
                    };
                    object? reused = null;
                    if (reenter)
                    {
                        receiveMemory.OnUnpin = SendReply;
                    }
                    Assert.Equal(1, sender.Send(new byte[] { 42 }));
                    Assert.Equal(1, await receive.WaitAsync(TestSettings.PassingTestTimeout));
                    Assert.Equal(42, receiveMemory.GetSpan()[0]);
                    if (!reenter)
                    {
                        SendReply();
                    }
                    Assert.Equal(1, await sent.Task.WaitAsync(TestSettings.PassingTestTimeout));
                    object cached = operationField.GetValue(context)!;
                    Assert.NotNull(cached);
                    if (!reenter)
                    {
                        Assert.Same(reused, cached);
                    }
                    byte[] reply = new byte[1];
                    Assert.Equal(1, await sender.ReceiveAsync(reply, SocketFlags.None).WaitAsync(TestSettings.PassingTestTimeout));
                    Assert.Equal(43, reply[0]);

                    Task<int> nextReceive = receiver.ReceiveAsync(receiveMemory.Memory, SocketFlags.None).AsTask();
                    Assert.False(nextReceive.IsCompleted);
                    Assert.Equal(1, sender.Send(new byte[] { 44 }));
                    Assert.Equal(1, await nextReceive.WaitAsync(TestSettings.PassingTestTimeout));
                    Assert.Equal(44, receiveMemory.GetSpan()[0]);
                    Assert.Same(cached, operationField.GetValue(context));
                    Assert.Equal(2, receiveMemory.UnpinCount);
                    Assert.Equal(1, sendMemory.UnpinCount);

                    void SendReply()
                    {
                        reused = operationField.GetValue(context);
                        if (reenter)
                        {
                            // Runtime-owned Unpin precedes logical completion: the receiving
                            // adapter cannot be cached or reused until its final callback.
                            Assert.Null(reused);
                        }
                        else
                        {
                            Assert.NotNull(reused);
                        }
                        sendMemory.GetSpan()[0] = 43;
                        Assert.True((bool)sendMethod.Invoke(context, new object[]
                        {
                            sendMemory.Memory, 0, 1, SocketFlags.None, 0, callback, CancellationToken.None
                        })!);
                    }
                }
            }, reenterFromUnpin.ToString(), CreateOptions(1)).Dispose();
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
                using (IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true))
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
                                binding.EnqueueForSubmission(new CallbackOperation(new IoUringRequest(IoUringOperationKind.Receive, pointer + i, 1), callback));
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
                (Socket Sender, Socket Receiver) pair = SocketTestExtensions.CreateConnectedSocketPair();
                using Socket sender = pair.Sender;
                using Socket receiver = pair.Receiver;
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
                (Socket Sender, Socket Receiver) pair = SocketTestExtensions.CreateConnectedSocketPair();
                using Socket sender = pair.Sender;
                using Socket receiver = pair.Receiver;

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
                using (IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true))
                using (ManualResetEventSlim firstCallback = new())
                using (ManualResetEventSlim releaseCallback = new())
                {
                    TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    int callbacks = 0;
                    IoUringOperation operation = EnqueueMultishot(binding, (result, buffer, more) =>
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
                    });
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

#pragma warning disable IL2075 // RemoteExecutor runs this implementation-specific test without trimming.
                        object queue = typeof(IoUringOperation).GetField("_pending",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(operation)!;
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
        public void Multishot_EmptyDatagramDoesNotEndReceive()
        {
            RemoteExecutor.Invoke(async () =>
            {
                using Socket receiver = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                using Socket sender = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                sender.Connect(receiver.LocalEndPoint!);
                using CancellationTokenSource cancellation = new(TestSettings.PassingTestTimeout);
                await using IAsyncEnumerator<IMemoryOwner<byte>> reader =
                    receiver.ReceiveMultishotAsync(cancellation.Token).GetAsyncEnumerator();
                Task<bool> next = reader.MoveNextAsync().AsTask();
                Assert.Equal(0, sender.Send(Array.Empty<byte>()));
                Assert.True(await next.WaitAsync(TestSettings.PassingTestTimeout));
                using (IMemoryOwner<byte> empty = reader.Current)
                {
                    Assert.True(empty.Memory.IsEmpty);
                }
                Assert.Equal(1, sender.Send(new byte[] { 42 }));
                Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TestSettings.PassingTestTimeout));
                using IMemoryOwner<byte> data = reader.Current;
                Assert.Equal(new byte[] { 42 }, data.Memory.ToArray());
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData("return")]
        [InlineData("cancel")]
        [InlineData("dispose")]
        public void Multishot_ExhaustedPoolParksUntilReturnOrCancellation(string ending)
        {
            RemoteInvokeOptions options = CreateOptions(1);
            options.StartInfo.Environment["DOTNET_IORING_RECV_BUFFER_COUNT"] = "1";
            RemoteExecutor.Invoke(async ending =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true))
                using (SemaphoreSlim available = new(0))
                {
                    ConcurrentQueue<IMemoryOwner<byte>> buffers = new();
                    TaskCompletionSource<int> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    IoUringOperation operation = EnqueueMultishot(binding, (result, buffer, more) =>
                    {
                        if (buffer is not null)
                        {
                            buffers.Enqueue(buffer);
                            available.Release();
                        }
                        if (!more)
                        {
                            completed.SetResult(result);
                        }
                    });
                    System.Reflection.FieldInfo tokenField = typeof(IoUringOperation).GetField("_nativeToken",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    ulong initialToken = (ulong)tokenField.GetValue(operation)!;
                    IMemoryOwner<byte>? retained = null;
                    try
                    {
                        Assert.Equal(1, sender.Send(new byte[] { 1 }));
                        Assert.True(await available.WaitAsync(TestSettings.PassingTestTimeout));
                        Assert.True(buffers.TryDequeue(out retained));
                        Assert.Equal(1, sender.Send(new byte[] { 2 }));
                        Assert.True(SpinWait.SpinUntil(() => (ulong)tokenField.GetValue(operation)! != initialToken,
                            TestSettings.PassingTestTimeout));
                        await Task.Delay(100);
                        ulong parkedToken = (ulong)tokenField.GetValue(operation)!;
                        await Task.Delay(200);
                        Assert.Equal(parkedToken, (ulong)tokenField.GetValue(operation)!);
                        Assert.False(completed.Task.IsCompleted);

                        IoUringOperation[] waiters = new IoUringOperation[3];
                        TaskCompletionSource<int>[] waiterCompletions = new TaskCompletionSource<int>[3];
                        for (int i = 0; i < waiters.Length; i++)
                        {
                            TaskCompletionSource<int> waiterCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                            waiterCompletions[i] = waiterCompleted;
                            waiters[i] = EnqueueMultishot(binding, (result, buffer, more) =>
                            {
                                buffer?.Dispose();
                                if (!more)
                                {
                                    waiterCompleted.SetResult(result);
                                }
                            });
                        }
                        System.Reflection.FieldInfo linkedField = typeof(IoUringOperation).GetField("_linked",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                        foreach (IoUringOperation waiter in waiters)
                        {
                            Assert.True(SpinWait.SpinUntil(() => (bool)linkedField.GetValue(waiter)!,
                                TestSettings.PassingTestTimeout));
                        }
                        foreach (int index in new[] { 1, 0, 2 })
                        {
                            waiters[index].RequestCancellation();
                            Assert.True(await waiterCompletions[index].Task.WaitAsync(TestSettings.PassingTestTimeout) < 0);
                        }

                        Assert.Equal(1, await receiver.SendAsync(new byte[] { 3 }, SocketFlags.None));
                        byte[] reply = new byte[1];
                        Assert.Equal(1, await sender.ReceiveAsync(reply, SocketFlags.None));
                        Assert.Equal(3, reply[0]);

                        if (ending == "return")
                        {
                            retained.Dispose();
                            Assert.True(await available.WaitAsync(TestSettings.PassingTestTimeout));
                            Assert.True(buffers.TryDequeue(out IMemoryOwner<byte> resumed));
                            using (resumed)
                            {
                                Assert.Equal(new byte[] { 2 }, resumed.Memory.ToArray());
                            }
                        }
                        else if (ending == "dispose")
                        {
                            binding.DisposeAndWait();
                            receiver.Dispose();
                        }
                        operation.RequestCancellation();
                        Assert.True(await completed.Task.WaitAsync(TestSettings.PassingTestTimeout) < 0);
                        if (ending != "return")
                        {
                            Assert.Equal(new byte[] { 1 }, retained.Memory.ToArray());
                        }
                    }
                    finally
                    {
                        operation.RequestCancellation();
                        retained?.Dispose();
                        await completed.Task.WaitAsync(TestSettings.PassingTestTimeout);
                        while (buffers.TryDequeue(out IMemoryOwner<byte> buffer))
                        {
                            buffer.Dispose();
                        }
                    }
                }
            }, ending, options).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void Multishot_DisposeOldLeaseDoesNotReturnReusedBuffer()
        {
            RemoteInvokeOptions options = CreateOptions(1);
            options.StartInfo.Environment["DOTNET_IORING_RECV_BUFFER_COUNT"] = "1";
            options.StartInfo.Environment["DOTNET_IORING_RECV_BUFFER_SIZE"] = "128";
            RemoteExecutor.Invoke(async () =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (CancellationTokenSource cancellation = new CancellationTokenSource(TestSettings.PassingTestTimeout))
                {
                    await using IAsyncEnumerator<IMemoryOwner<byte>> reader =
                        receiver.ReceiveMultishotAsync(cancellation.Token).GetAsyncEnumerator();
                    sender.Send(new byte[] { 1 });
                    Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TestSettings.PassingTestTimeout));
                    IMemoryOwner<byte> first = reader.Current;
                    first.Dispose();
                    sender.Send(new byte[] { 2 });
                    Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TestSettings.PassingTestTimeout));
                    using IMemoryOwner<byte> second = reader.Current;
                    Assert.NotSame(first, second);
                    first.Dispose();
                    Assert.Throws<ObjectDisposedException>(() => first.Memory);
                    sender.Send(new byte[] { 3 });
                    Task<bool> next = reader.MoveNextAsync().AsTask();
                    Assert.False(next.IsCompleted);
                    Assert.Equal(2, second.Memory.Span[0]);
                    second.Dispose();
                    Assert.True(await next.WaitAsync(TestSettings.PassingTestTimeout));
                    using IMemoryOwner<byte> third = reader.Current;
                    Assert.Equal(3, third.Memory.Span[0]);
                }
            }, options).Dispose();
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
                using (IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true))
                using (ManualResetEventSlim firstCallback = new())
                using (ManualResetEventSlim releaseCallback = new())
                {
                    AsyncLocal<int> context = new();
                    TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    int callbacks = 0;
                    IoUringOperation operation = EnqueueMultishot(binding, (result, buffer, more) =>
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
                    });
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

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void SocketUnpin_CanPerformSynchronousIoInSameDirection(bool send)
        {
            RemoteExecutor.Invoke(async sendText =>
            {
                bool sending = bool.Parse(sendText);
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (TrackingMemoryManager memory = new(sending ? 2 * 1024 * 1024 : 1))
                {
                    int unpinCallbacks = 0;
                    if (sending)
                    {
                        sender.SendBufferSize = 4096;
                        memory.GetSpan().Fill(42);
                        memory.OnUnpin = () =>
                        {
                            Assert.Equal(1, sender.Send(new byte[] { 43 }));
                            unpinCallbacks++;
                        };
                        Task<int> sendingTask = sender.SendAsync(memory.Memory).AsTask();
                        Assert.False(sendingTask.IsCompleted);
                        Assert.Equal(1, memory.PinCount);
                        byte[] received = new byte[memory.Memory.Length + 1];
                        Task readingTask = ReadAll();
                        Assert.Equal(memory.Memory.Length, await sendingTask.WaitAsync(TestSettings.PassingTestTimeout));
                        await readingTask.WaitAsync(TestSettings.PassingTestTimeout);
                        AssertExtensions.SequenceEqual(memory.GetSpan(), received.AsSpan(0, memory.Memory.Length));
                        Assert.Equal(43, received[received.Length - 1]);

                        async Task ReadAll()
                        {
                            int count = 0;
                            while (count < received.Length)
                            {
                                int read = await receiver.ReceiveAsync(received.AsMemory(count));
                                Assert.True(read > 0);
                                count += read;
                            }
                        }
                    }
                    else
                    {
                        byte[] next = new byte[1];
                        memory.OnUnpin = () =>
                        {
                            Assert.Equal(1, receiver.Receive(next));
                            unpinCallbacks++;
                        };
                        Task<int> receiving = receiver.ReceiveAsync(memory.Memory).AsTask();
                        Assert.False(receiving.IsCompleted);
                        Assert.Equal(1, memory.PinCount);
                        Assert.Equal(2, sender.Send(new byte[] { 42, 43 }));
                        Assert.Equal(1, await receiving.WaitAsync(TestSettings.PassingTestTimeout));
                        Assert.Equal(42, memory.GetSpan()[0]);
                        Assert.Equal(43, next[0]);
                    }
                    Assert.Equal(1, unpinCallbacks);
                    Assert.Equal(1, memory.PinCount);
                    Assert.Equal(1, memory.UnpinCount);
                }
            }, send.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void LiveOperation_PublicExecuteCannotDispatchOrReleaseNativeResources()
        {
            RemoteExecutor.Invoke(async () =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (TrackingMemoryManager memory = new())
                using (IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true))
                using (ManualResetEventSlim entered = new())
                using (ManualResetEventSlim release = new())
                {
                    TaskCompletionSource<Exception?> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    int processed = 0;
                    int activeCallbacks = 0;
                    CompletionActionOperation operation = new(new IoUringRequest(IoUringOperationKind.Receive, memory.Memory),
                        completion =>
                        {
                            Assert.Equal(1, completion.Result);
                            Assert.Equal(42, memory.GetSpan()[0]);
                            Assert.Equal(1, Interlocked.Increment(ref activeCallbacks));
                            Interlocked.Increment(ref processed);
                            try
                            {
                                entered.Set();
                                Assert.True(release.Wait(TestSettings.PassingTestTimeout));
                                return IoUringCompletionAction.Complete;
                            }
                            finally
                            {
                                Interlocked.Decrement(ref activeCallbacks);
                            }
                        }, error => completed.SetResult(error));
                    binding.EnqueueForSubmission(operation);
                    try
                    {
                        await Task.Factory.StartNew(() =>
                        {
                            Assert.False(Thread.CurrentThread.IsThreadPoolThread);
                            Assert.Throws<InvalidOperationException>(() => ((IThreadPoolWorkItem)operation).Execute());
                        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                            .WaitAsync(TestSettings.PassingTestTimeout);
                        await InvokeConcurrently();
                        Assert.Equal(0, Volatile.Read(ref processed));
                        Assert.False(completed.Task.IsCompleted);
                        Assert.Equal(1, memory.PinCount);
                        Assert.Equal(0, memory.UnpinCount);

                        Assert.Equal(1, sender.Send(new byte[] { 42 }));
                        Assert.True(entered.Wait(TestSettings.PassingTestTimeout));
                        await InvokeConcurrently();
                        Assert.Equal(1, Volatile.Read(ref processed));
                        Assert.Equal(1, Volatile.Read(ref activeCallbacks));
                        Assert.False(completed.Task.IsCompleted);
                        Assert.Equal(0, memory.UnpinCount);
                    }
                    finally
                    {
                        release.Set();
                    }
                    Assert.Null(await completed.Task.WaitAsync(TestSettings.PassingTestTimeout));
                    Assert.Equal(1, processed);
                    Assert.Equal(0, activeCallbacks);
                    Assert.Equal(1, memory.PinCount);
                    Assert.Equal(1, memory.UnpinCount);

                    Task InvokeConcurrently()
                    {
                        Task[] invocations = new Task[16];
                        for (int i = 0; i < invocations.Length; i++)
                        {
                            invocations[i] = Task.Run(() => ((IThreadPoolWorkItem)operation).Execute());
                        }
                        return Task.WhenAll(invocations).WaitAsync(TestSettings.PassingTestTimeout);
                    }
                }
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void ManagedRequest_PinCallbacksCanSynchronouslyDisposeBinding(bool disposeOnUnpin)
        {
            RemoteExecutor.Invoke(async unpinText =>
            {
                bool onUnpin = bool.Parse(unpinText);
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (TrackingMemoryManager first = new())
                using (TrackingMemoryManager second = new())
                using (IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true))
                {
                    TaskCompletionSource<Exception?> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    int processed = 0;
                    int disposalCallbacks = 0;
                    Action disposeBinding = () =>
                    {
                        binding.DisposeAndWait();
                        disposalCallbacks++;
                    };
                    if (onUnpin)
                    {
                        first.OnUnpin = disposeBinding;
                    }
                    else
                    {
                        first.OnPin = disposeBinding;
                    }
                    CompletionActionOperation operation = new(new IoUringRequest(IoUringOperationKind.Receive, first.Memory),
                        completion =>
                        {
                            Assert.Equal(1, completion.Result);
                            return ++processed == 1 && onUnpin
                                ? IoUringCompletionAction.Resubmit(new IoUringRequest(IoUringOperationKind.Receive, second.Memory))
                                : IoUringCompletionAction.Complete;
                        }, error => completed.SetResult(error));

                    if (onUnpin)
                    {
                        binding.EnqueueForSubmission(operation);
                        Assert.Equal(1, sender.Send(new byte[] { 42 }));
                        Assert.IsType<ObjectDisposedException>(await completed.Task.WaitAsync(TestSettings.PassingTestTimeout));
                    }
                    else
                    {
                        await Task.Run(() => Assert.Throws<ObjectDisposedException>(() => binding.EnqueueForSubmission(operation)))
                            .WaitAsync(TestSettings.PassingTestTimeout);
                        Assert.False(completed.Task.IsCompleted);
                    }
                    Assert.Equal(1, disposalCallbacks);
                    Assert.Equal(1, first.PinCount);
                    Assert.Equal(1, first.UnpinCount);
                    Assert.Equal(onUnpin ? 1 : 0, second.PinCount);
                    Assert.Equal(second.PinCount, second.UnpinCount);

                    // A fresh binding and reuse of the same operation verify that both native
                    // admission and logical-operation state rolled back after the callback closed it.
                    using IoRingBoundHandle rebound = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true);
                    completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    processed = 0;
                    rebound.EnqueueForSubmission(operation);
                    byte[] payload = onUnpin ? new byte[] { 43, 44 } : new byte[] { 43 };
                    Assert.Equal(payload.Length, sender.Send(payload));
                    Assert.Null(await completed.Task.WaitAsync(TestSettings.PassingTestTimeout));
                    Assert.Equal(2, first.PinCount);
                    Assert.Equal(2, first.UnpinCount);
                    Assert.Equal(onUnpin ? 2 : 0, second.PinCount);
                    Assert.Equal(second.PinCount, second.UnpinCount);
                    Assert.Equal(1, disposalCallbacks);
                }
            }, disposeOnUnpin.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void ManagedRequest_RollbackHookFailurePreservesSubmissionErrorAndReleasesPin()
        {
            RemoteExecutor.Invoke(async () =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (TrackingMemoryManager memory = new())
                using (IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true))
                {
                    InvalidOperationException hookFailure = new("Rollback hook failed.");
                    bool failHook = true;
                    int completingCalls = 0;
                    int completedCalls = 0;
                    TaskCompletionSource<Exception?> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    memory.OnPin = () => binding.DisposeAndWait();
                    CompletionActionOperation operation = new(new IoUringRequest(IoUringOperationKind.Receive, memory.Memory),
                        completion =>
                        {
                            Assert.Equal(1, completion.Result);
                            return IoUringCompletionAction.Complete;
                        }, error =>
                        {
                            completedCalls++;
                            completed.SetResult(error);
                        }, () =>
                        {
                            completingCalls++;
                            if (failHook)
                            {
                                throw hookFailure;
                            }
                        });
                    AggregateException error = await Task.Run(() =>
                        Assert.Throws<AggregateException>(() => binding.EnqueueForSubmission(operation)))
                        .WaitAsync(TestSettings.PassingTestTimeout);
                    IReadOnlyCollection<Exception> failures = error.Flatten().InnerExceptions;
                    Assert.Equal(2, failures.Count);
                    Assert.Contains(hookFailure, failures);
                    Assert.Contains(failures, failure => failure is ObjectDisposedException);
                    Assert.Equal(1, completingCalls);
                    Assert.Equal(0, completedCalls);
                    Assert.False(completed.Task.IsCompleted);
                    Assert.Equal(1, memory.PinCount);
                    Assert.Equal(1, memory.UnpinCount);

                    failHook = false;
                    using IoRingBoundHandle rebound = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true);
                    rebound.EnqueueForSubmission(operation);
                    Assert.Equal(1, sender.Send(new byte[] { 42 }));
                    Assert.Null(await completed.Task.WaitAsync(TestSettings.PassingTestTimeout));
                    Assert.Equal(2, completingCalls);
                    Assert.Equal(1, completedCalls);
                    Assert.Equal(2, memory.PinCount);
                    Assert.Equal(2, memory.UnpinCount);
                }
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void ManagedRequest_SlicedContinuationsShareOnePin()
        {
            RemoteExecutor.Invoke(async () =>
            {
                const int Length = 32;
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (TrackingMemoryManager memory = new(Length))
                using (IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true))
                {
                    TaskCompletionSource<Exception?> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    IoUringRequest request = new(IoUringOperationKind.Receive, memory.Memory);
                    int received = 0;
                    CompletionActionOperation operation = new(request, completion =>
                    {
                        Assert.Equal(1, completion.Result);
                        Assert.False(completion.HasMore);
                        Assert.Equal(1, memory.PinCount);
                        Assert.Equal(0, memory.UnpinCount);
                        Assert.Equal((byte)received, memory.GetSpan()[received]);
                        received++;
                        if (received == Length)
                        {
                            return IoUringCompletionAction.Complete;
                        }

                        request = request.SliceBuffer(1, Length - received).WithOffset(-1);
                        Assert.Equal(1, sender.Send(new[] { (byte)received }));
                        return IoUringCompletionAction.Resubmit(request);
                    }, error => completed.SetResult(error));
                    binding.EnqueueForSubmission(operation);
                    Assert.Equal(1, memory.PinCount);
                    Assert.Equal(0, memory.UnpinCount);
                    Assert.Equal(1, sender.Send(new byte[] { 0 }));
                    Assert.Null(await completed.Task.WaitAsync(TestSettings.PassingTestTimeout));
                    Assert.Equal(Length, received);
                    Assert.Equal(1, memory.PinCount);
                    Assert.Equal(1, memory.UnpinCount);
                    for (int i = 0; i < Length; i++)
                    {
                        Assert.Equal((byte)i, memory.GetSpan()[i]);
                    }
                }
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData("throw")]
        [InlineData("fail")]
        [InlineData("complete")]
        [InlineData("resubmit-active")]
        [InlineData("continue-terminal")]
        public void CompletionDecision_DrainsNativeRequestBeforeReleasingPin(string decision)
        {
            RemoteExecutor.Invoke(async decision =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (TrackingMemoryManager memory = new())
                using (IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true))
                {
                    TaskCompletionSource<Exception?> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    InvalidOperationException failure = new("Custom completion failed.");
                    IoUringRequest request = new(IoUringOperationKind.Receive, memory.Memory);
                    IoUringRequest poll;
                    unsafe
                    {
                        poll = TestPollOperation.CreateRequest(IoUringPollEvents.Readable);
                    }
                    int callbacks = 0;
                    CompletionActionOperation operation = new(request, completion =>
                    {
                        Assert.Equal(1, memory.PinCount);
                        Assert.Equal(0, memory.UnpinCount);
                        if (++callbacks == 1)
                        {
                            Assert.Equal(1, completion.Result);
                            Assert.False(completion.HasMore);
                            if (decision == "continue-terminal")
                            {
                                return IoUringCompletionAction.Continue;
                            }
                            Assert.Equal(1, sender.Send(new byte[] { 43 }));
                            return IoUringCompletionAction.Resubmit(poll);
                        }

                        Assert.Equal(2, callbacks);
                        Assert.True(completion.HasMore);
                        Assert.NotEqual(0, completion.Result & (int)IoUringPollEvents.Readable);
                        return decision switch
                        {
                            "throw" => throw failure,
                            "fail" => IoUringCompletionAction.Fail(failure),
                            "complete" => IoUringCompletionAction.Complete,
                            "resubmit-active" => IoUringCompletionAction.Resubmit(request),
                            _ => throw new InvalidOperationException(decision),
                        };
                    }, error => completed.SetResult(error));

#pragma warning disable IL2075 // The RemoteExecutor process is untrimmed.
                    System.Reflection.PropertyInfo nativePending = typeof(IoUringOperation).GetProperty("IsNativePending",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
#pragma warning restore IL2075
                    memory.OnUnpin = () => Assert.False((bool)nativePending.GetValue(operation)!);
                    binding.EnqueueForSubmission(operation);
                    Assert.Equal(1, sender.Send(new byte[] { 42 }));
                    Exception? error = await completed.Task.WaitAsync(TestSettings.PassingTestTimeout);
                    if (decision is "throw" or "fail")
                    {
                        Assert.Same(failure, error);
                    }
                    else if (decision == "complete")
                    {
                        Assert.Null(error);
                    }
                    else
                    {
                        Assert.IsType<InvalidOperationException>(error);
                    }
                    Assert.Equal(decision == "continue-terminal" ? 1 : 2, callbacks);
                    Assert.False((bool)nativePending.GetValue(operation)!);
                    Assert.Equal(1, memory.PinCount);
                    Assert.Equal(1, memory.UnpinCount);
                }
            }, decision, CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void ManagedRequest_FinalCallbackCanReuseOperation(bool multishotContinuation)
        {
            RemoteExecutor.Invoke(async multishotText =>
            {
                const int Iterations = 32;
                bool multishot = bool.Parse(multishotText);
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (TrackingMemoryManager memory = new())
                using (IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true))
                {
                    TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    int callbacks = 0;
                    bool polling = false;
                    IoUringRequest poll;
                    unsafe
                    {
                        poll = TestPollOperation.CreateRequest(IoUringPollEvents.Writable);
                    }
                    CompletionActionOperation? operation = null;
                    operation = new CompletionActionOperation(new IoUringRequest(IoUringOperationKind.Receive, memory.Memory),
                        completion =>
                        {
                            if (polling)
                            {
                                Assert.True(completion.HasMore);
                                Assert.NotEqual(0, completion.Result & (int)IoUringPollEvents.Writable);
                                return IoUringCompletionAction.Complete;
                            }
                            Assert.Equal(1, completion.Result);
                            Assert.Equal(42, memory.GetSpan()[0]);
                            if (multishot)
                            {
                                polling = true;
                                return IoUringCompletionAction.Resubmit(poll);
                            }
                            return IoUringCompletionAction.Complete;
                        }, error =>
                        {
                            try
                            {
                                Assert.Null(error);
                                int count = ++callbacks;
                                Assert.Equal(count, memory.PinCount);
                                Assert.Equal(count, memory.UnpinCount);
                                if (count == Iterations)
                                {
                                    completed.SetResult();
                                }
                                else
                                {
                                    polling = false;
                                    binding.EnqueueForSubmission(operation!);
                                    Assert.Equal(1, sender.Send(new byte[] { 42 }));
                                }
                            }
                            catch (Exception failure)
                            {
                                completed.TrySetException(failure);
                            }
                        });
                    binding.EnqueueForSubmission(operation);
                    Assert.Equal(1, sender.Send(new byte[] { 42 }));
                    await completed.Task.WaitAsync(TestSettings.PassingTestTimeout);
                    Assert.Equal(Iterations, callbacks);
                    Assert.Equal(Iterations, memory.PinCount);
                    Assert.Equal(Iterations, memory.UnpinCount);
                }
            }, multishotContinuation.ToString(), CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void TypedPoll_NotifiesRepeatedlyWithoutConsumingData()
        {
            RemoteExecutor.Invoke(async () =>
            {
                const int Notifications = 3;
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true))
                {
                    TaskCompletionSource[] ready = new TaskCompletionSource[Notifications];
                    for (int i = 0; i < ready.Length; i++)
                    {
                        ready[i] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    TaskCompletionSource<Exception?> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    int callbacks = 0;
                    TestPollOperation? operation = null;
                    operation = new TestPollOperation(IoUringPollEvents.Readable, events =>
                    {
                        Assert.Equal(IoUringPollEvents.Readable, events & IoUringPollEvents.Readable);
                        Assert.InRange(callbacks, 0, Notifications - 1);
                        byte[] value = new byte[1];
                        Assert.Equal(1, receiver.Receive(value));
                        Assert.Equal((byte)callbacks, value[0]);
                        ready[callbacks++].SetResult();
                        if (callbacks == Notifications)
                        {
                            operation!.RequestCancellation();
                        }
                    }, error => completed.SetResult(error));
                    binding.EnqueueForSubmission(operation);
                    for (int i = 0; i < Notifications; i++)
                    {
                        Assert.Equal(1, sender.Send(new[] { (byte)i }));
                        await ready[i].Task.WaitAsync(TestSettings.PassingTestTimeout);
                    }
                    Assert.IsType<OperationCanceledException>(await completed.Task.WaitAsync(TestSettings.PassingTestTimeout));
                    Assert.Equal(Notifications, callbacks);
                }
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void TypedAccept_TransfersUsableOwnedSocketHandles()
        {
            RemoteExecutor.Invoke(async () =>
            {
                const int Connections = 8;
                using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(Connections);
                using IoRingBoundHandle binding = IoUring.Bind(listener.SafeHandle, ownsFileDescriptor: true);
                List<Socket> clients = new();
                ConcurrentQueue<SafeSocketHandle> accepted = new();
                TaskCompletionSource<Exception?> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                int callbacks = 0;
                TestAcceptOperation? operation = null;
                operation = new TestAcceptOperation(handle =>
                {
                    Assert.False(handle.IsInvalid);
                    accepted.Enqueue(handle);
                    if (++callbacks == Connections)
                    {
                        operation!.RequestCancellation();
                    }
                }, error => completed.SetResult(error));
                try
                {
                    binding.EnqueueForSubmission(operation);
                    for (int i = 0; i < Connections; i++)
                    {
                        Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        clients.Add(client);
                        await client.ConnectAsync(listener.LocalEndPoint!);
                        Assert.Equal(1, client.Send(new[] { (byte)i }));
                    }
                    Assert.IsType<OperationCanceledException>(await completed.Task.WaitAsync(TestSettings.PassingTestTimeout));
                    Assert.Equal(Connections, callbacks);
                    HashSet<EndPoint> peers = new();
                    while (accepted.TryDequeue(out SafeSocketHandle? handle))
                    {
                        using (handle)
                        using (Socket socket = new(handle))
                        {
                            Assert.True(peers.Add(socket.RemoteEndPoint!));
                            byte[] value = new byte[1];
                            Assert.Equal(1, await socket.ReceiveAsync(value.AsMemory()).AsTask().WaitAsync(TestSettings.PassingTestTimeout));
                            Assert.InRange(value[0], (byte)0, (byte)(Connections - 1));
                            Assert.Equal(clients[value[0]].LocalEndPoint, socket.RemoteEndPoint);
                        }
                        Assert.True(handle.IsClosed);
                    }
                    Assert.Equal(Connections, peers.Count);
                }
                finally
                {
                    binding.DisposeAndWait();
                    foreach (Socket client in clients)
                    {
                        client.Dispose();
                    }
                    while (accepted.TryDequeue(out SafeSocketHandle? handle))
                    {
                        handle.Dispose();
                    }
                }
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData("throw")]
        [InlineData("dispose")]
        [InlineData("unclaimed")]
        [InlineData("discard-throws")]
        public void AcceptTermination_ClosesOwnedAndUndeliveredDescriptors(string ending)
        {
            RemoteExecutor.Invoke(async ending =>
            {
                const int Connections = 8;
                using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(Connections);
                using IoRingBoundHandle binding = IoUring.Bind(listener.SafeHandle, ownsFileDescriptor: true);
                using ManualResetEventSlim entered = new();
                using ManualResetEventSlim release = new();
                List<Socket> clients = new();
                List<int> descriptors = new();
                InvalidOperationException failure = new("Accept callback failed.");
                TaskCompletionSource<Exception?> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                int callbacks = 0;
                int firstDescriptor = -1;
                IoUringOperation operation;
                if (ending is "unclaimed" or "discard-throws")
                {
                    IoUringRequest request;
                    unsafe
                    {
                        request = TestAcceptOperation.CreateRequest();
                    }
                    operation = new CompletionActionOperation(request, completion =>
                    {
                        Assert.True(completion.Result >= 0);
                        using SafeSocketHandle handle = new((IntPtr)completion.Result, ownsHandle: true);
                        Assert.Equal(1, ++callbacks);
                        firstDescriptor = completion.Result;
                        entered.Set();
                        Assert.True(release.Wait(TestSettings.PassingTestTimeout));
                        throw failure;
                    }, error => completed.SetResult(error), discarded: completion =>
                    {
                        TestAcceptOperation.Discard(completion);
                        if (ending == "discard-throws" && completion.Result >= 0)
                        {
                            throw new IOException("Discard callback failed after releasing the descriptor.");
                        }
                    });
                }
                else
                {
                    operation = new TestAcceptOperation(handle =>
                    {
                        using (handle)
                        {
                            if (++callbacks == 1)
                            {
                                firstDescriptor = (int)handle.DangerousGetHandle();
                                entered.Set();
                                Assert.True(release.Wait(TestSettings.PassingTestTimeout));
                                if (ending == "throw")
                                {
                                    throw failure;
                                }
                                binding.DisposeAndWait();
                                listener.Dispose();
                            }
                        }
                    }, error => completed.SetResult(error));
                }
                try
                {
                    for (int i = 0; i < Connections; i++)
                    {
                        Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        clients.Add(client);
                        await client.ConnectAsync(listener.LocalEndPoint!);
                    }
                    binding.EnqueueForSubmission(operation);
                    Assert.True(entered.Wait(TestSettings.PassingTestTimeout));
#pragma warning disable IL2075 // The RemoteExecutor process is untrimmed.
                    IEnumerable<IoUringCompletion> pending = (IEnumerable<IoUringCompletion>)typeof(IoUringOperation)
                        .GetField("_pending", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .GetValue(operation)!;
#pragma warning restore IL2075
                    Assert.True(SpinWait.SpinUntil(() =>
                    {
                        descriptors.Clear();
                        descriptors.Add(firstDescriptor);
                        foreach (IoUringCompletion completion in pending)
                        {
                            if (completion.Result >= 0)
                            {
                                descriptors.Add(completion.Result);
                            }
                        }
                        return descriptors.Count == Connections;
                    }, TestSettings.PassingTestTimeout));
                    Assert.Equal(Connections, new HashSet<int>(descriptors).Count);
                    Dictionary<int, string> socketTargets = new();
                    foreach (int descriptor in descriptors)
                    {
                        Assert.True(File.Exists($"/proc/self/fd/{descriptor}"));
                        socketTargets.Add(descriptor, new FileInfo($"/proc/self/fd/{descriptor}").LinkTarget!);
                    }
                    release.Set();
                    Exception? error = await completed.Task.WaitAsync(TestSettings.PassingTestTimeout);
                    if (ending == "dispose")
                    {
                        Assert.IsType<OperationCanceledException>(error);
                        Assert.InRange(callbacks, 1, Connections);
                    }
                    else if (ending == "discard-throws")
                    {
                        AggregateException failures = Assert.IsType<AggregateException>(error).Flatten();
                        Assert.Contains(failure, failures.InnerExceptions);
                        Assert.Equal(Connections, failures.InnerExceptions.Count);
                        Assert.Equal(1, callbacks);
                    }
                    else
                    {
                        Assert.Same(failure, error);
                        Assert.Equal(1, callbacks);
                    }
                    foreach (int descriptor in descriptors)
                    {
                        // Exception handling can load assemblies and reuse a closed descriptor.
                        Assert.NotEqual(socketTargets[descriptor], new FileInfo($"/proc/self/fd/{descriptor}").LinkTarget);
                    }
                }
                finally
                {
                    release.Set();
                    binding.DisposeAndWait();
                    foreach (Socket client in clients)
                    {
                        client.Dispose();
                    }
                }
            }, ending, CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void ProvidedBuffers_EnforceTransferAndReturnUntakenBuffers(bool takeBuffer)
        {
            RemoteInvokeOptions options = CreateOptions(1);
            options.StartInfo.Environment["DOTNET_IORING_RECV_BUFFER_COUNT"] = "1";
            RemoteExecutor.Invoke(async takeText =>
            {
                const int Iterations = 16;
                bool take = bool.Parse(takeText);
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (IoRingBoundHandle binding = IoUring.Bind(receiver.SafeHandle, ownsFileDescriptor: true))
                using (SemaphoreSlim ready = new(0))
                {
                    TaskCompletionSource<Exception?> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    IMemoryOwner<byte>? lastBuffer = null;
                    int callbacks = 0;
                    IoUringRequest request;
                    unsafe
                    {
                        request = new IoUringRequest(IoUringOperationKind.ReceiveMultishot, null, 0);
                    }
                    CompletionActionOperation? operation = null;
                    operation = new CompletionActionOperation(request, completion =>
                    {
                        const int NoBuffers = 105;
                        if (completion.Result == -NoBuffers)
                        {
                            return IoUringCompletionAction.Resubmit(request);
                        }
                        Assert.Equal(1, completion.Result);
                        ++callbacks;
                        if (take)
                        {
                            IMemoryOwner<byte> buffer = Assert.IsAssignableFrom<IMemoryOwner<byte>>(operation!.TakeCurrentBuffer());
                            Assert.Equal(42, buffer.Memory.Span[0]);
                            Assert.Throws<InvalidOperationException>(() => operation.TakeCurrentBuffer());
                            if (callbacks == Iterations)
                            {
                                lastBuffer = buffer;
                            }
                            else
                            {
                                buffer.Dispose();
                            }
                        }
                        ready.Release();
                        return callbacks == Iterations ? IoUringCompletionAction.Complete :
                            completion.HasMore ? IoUringCompletionAction.Continue : IoUringCompletionAction.Resubmit(request);
                    }, error => completed.SetResult(error));
                    Assert.Throws<InvalidOperationException>(() => operation.TakeCurrentBuffer());
                    binding.EnqueueForSubmission(operation);
                    try
                    {
                        for (int i = 0; i < Iterations; i++)
                        {
                            Assert.Equal(1, sender.Send(new byte[] { 42 }));
                            Assert.True(await ready.WaitAsync(TestSettings.PassingTestTimeout));
                            Assert.Throws<InvalidOperationException>(() => operation.TakeCurrentBuffer());
                        }
                        Assert.Null(await completed.Task.WaitAsync(TestSettings.PassingTestTimeout));
                        Assert.Equal(Iterations, callbacks);
                        if (take)
                        {
                            Assert.NotNull(lastBuffer);
                            Assert.Equal(42, lastBuffer.Memory.Span[0]);
                        }
                    }
                    finally
                    {
                        operation.RequestCancellation();
                        lastBuffer?.Dispose();
                    }
                }
            }, takeBuffer.ToString(), options).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData(IoUringSubmissionOptions.None)]
        [InlineData(IoUringSubmissionOptions.ForceAsync)]
        public void NativeNop_CompletesAndSupportsReentrantReuse(IoUringSubmissionOptions options)
        {
            RemoteExecutor.Invoke(async optionsText =>
            {
                const byte NopOpcode = 0;
                const int Iterations = 32;
                using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using IoRingBoundHandle binding = IoUring.Bind(socket.SafeHandle, ownsFileDescriptor: true);
                Assert.True(binding.IsOperationSupported(NopOpcode));
                IoUringRequest request;
                unsafe
                {
                    IoUringSubmission submission = new(NopOpcode, null, 0,
                        options: Enum.Parse<IoUringSubmissionOptions>(optionsText));
                    request = IoUringRequest.CreateUnsafe(in submission);
                }
                TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                AsyncLocal<int> context = new();
                int processed = 0;
                int callbacks = 0;
                CompletionActionOperation? operation = null;
                operation = new CompletionActionOperation(request, completion =>
                {
                    Assert.Equal(0, context.Value);
                    Assert.Null(SynchronizationContext.Current);
                    Assert.Equal(0, completion.Result);
                    Assert.Equal(0u, completion.Flags);
                    Assert.False(completion.HasMore);
                    Assert.False(completion.IsNotification);
                    Assert.Equal(0UL, completion.Extra1);
                    Assert.Equal(0UL, completion.Extra2);
                    processed++;
                    context.Value = 42;
                    SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                    return IoUringCompletionAction.Complete;
                }, error =>
                {
                    try
                    {
                        Assert.Null(error);
                        Assert.Equal(0, context.Value);
                        Assert.Null(SynchronizationContext.Current);
                        if (++callbacks == Iterations)
                        {
                            completed.SetResult();
                        }
                        else
                        {
                            binding.EnqueueForSubmission(operation!);
                        }
                    }
                    catch (Exception failure)
                    {
                        completed.TrySetException(failure);
                    }
                });
                binding.EnqueueForSubmission(operation);
                await completed.Task.WaitAsync(TestSettings.PassingTestTimeout);
                Assert.Equal(Iterations, processed);
                Assert.Equal(Iterations, callbacks);
                binding.DisposeAndWait();
                Assert.Throws<ObjectDisposedException>(() => binding.IsOperationSupported(NopOpcode));
            }, options.ToString(), CreateOptions(1)).Dispose();
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        [InlineData(32)]
        [InlineData(64)]
        public unsafe void NativeSubmission_RejectsUnsafeSchedulingOptions(int options)
        {
            Assert.Throws<ArgumentOutOfRangeException>("options", () =>
                new IoUringSubmission(0, null, 0, options: (IoUringSubmissionOptions)options));
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData((byte)0, 0, 1, 0UL)]
        [InlineData((byte)0, 0, 0, 1UL)]
        [InlineData((byte)6, 2, 0, 0UL)]
        [InlineData((byte)27, 0, 2, 0UL)]
        [InlineData((byte)13, 1, 1, 0UL)]
        [InlineData((byte)13, 0, 2, 0UL)]
        public void NativeSubmission_RejectsUnsupportedOwnershipModifiers(byte opcode, int length, int priority, ulong address3)
        {
            RemoteExecutor.Invoke((opcodeText, fieldsText) =>
            {
                string[] fields = fieldsText.Split(',');
                unsafe
                {
                    IoUringSubmission submission = new(byte.Parse(opcodeText), null, int.Parse(fields[0]),
                        priority: ushort.Parse(fields[1]), address3: ulong.Parse(fields[2]));
                    Assert.Throws<ArgumentException>("submission", () => IoUringRequest.CreateUnsafe(in submission));
                }
            }, opcode.ToString(), $"{length},{priority},{address3}", CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void NativeSubmission_OpcodeProbeDoesNotAuthorizeSharedRingOperations()
        {
            RemoteExecutor.Invoke(() =>
            {
                const byte CloseOpcode = 19;
                using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using IoRingBoundHandle binding = IoUring.Bind(socket.SafeHandle, ownsFileDescriptor: true);
                Assert.True(binding.IsOperationSupported(CloseOpcode));
                Assert.False(binding.IsOperationSupported(byte.MaxValue));
                // Fixed reads, timeout/cancel, close, registration updates, buffer-pool
                // changes, and messaging another ring do not use this operation's ownership model.
                byte[] forbiddenOpcodes = [4, 11, 14, CloseOpcode, 20, 31, 32, 40, byte.MaxValue];
                foreach (byte opcode in forbiddenOpcodes)
                {
                    unsafe
                    {
                        IoUringSubmission submission = new(opcode, null, 0);
                        Assert.Throws<PlatformNotSupportedException>(() => IoUringRequest.CreateUnsafe(in submission));
                    }
                }
                Assert.False(socket.SafeHandle.IsClosed);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalFact(nameof(IsSupported))]
        public void NativeSubmission_WithOffsetRejectsOperationSpecificOffset()
        {
            RemoteExecutor.Invoke(() =>
            {
                unsafe
                {
                    IoUringSubmission submission = new(0, null, 0);
                    IoUringRequest request = IoUringRequest.CreateUnsafe(in submission);
                    Assert.Throws<InvalidOperationException>(() => request.WithOffset(123));
                }
            }, CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData("memory")]
        [InlineData("readonly-memory")]
        [InlineData("pointer")]
        [InlineData("gather")]
        [InlineData("native-send")]
        [InlineData("native-sendmsg")]
        public void OrdinarySend_RejectsSocketErrorQueueZeroCopyBeforeSubmission(string constructor)
        {
            RemoteExecutor.Invoke(constructor =>
            {
                const int MsgZeroCopy = 0x04000000;
                const byte SendOpcode = 26;
                const byte SendMessageOpcode = 9;
                using TrackingMemoryManager memory = new();
                unsafe
                {
                    switch (constructor)
                    {
                        case "memory":
                            Assert.Throws<ArgumentException>("flags", () =>
                                new IoUringRequest(IoUringOperationKind.Send, memory.Memory, flags: MsgZeroCopy));
                            break;
                        case "readonly-memory":
                            Assert.Throws<ArgumentException>("flags", () =>
                                new IoUringRequest(IoUringOperationKind.Send, (ReadOnlyMemory<byte>)memory.Memory,
                                    flags: MsgZeroCopy));
                            break;
                        case "pointer":
                            Assert.Throws<ArgumentException>("flags", () =>
                                new IoUringRequest(IoUringOperationKind.Send, null, 0, flags: MsgZeroCopy));
                            break;
                        case "gather":
                            SendVector vector = default;
                            nint vectors = (nint)(&vector);
                            Assert.Throws<ArgumentException>("flags", () =>
                                new IoUringRequest(IoUringOperationKind.SendGather, (void*)vectors, 1, flags: MsgZeroCopy));
                            break;
                        case "native-send":
                        case "native-sendmsg":
                            IoUringSubmission submission = new(
                                constructor == "native-send" ? SendOpcode : SendMessageOpcode,
                                null, 0, operationFlags: MsgZeroCopy);
                            Assert.Throws<ArgumentException>("submission", () => IoUringRequest.CreateUnsafe(in submission));
                            break;
                        default:
                            throw new InvalidOperationException(constructor);
                    }

                    _ = new IoUringRequest(IoUringOperationKind.SendZeroCopy, memory.Memory);
                    _ = new IoUringRequest(IoUringOperationKind.SendZeroCopy, (ReadOnlyMemory<byte>)memory.Memory);
                    _ = new IoUringRequest(IoUringOperationKind.SendZeroCopy, null, 0);
                }
                Assert.Equal(0, memory.PinCount);
                Assert.Equal(0, memory.UnpinCount);
            }, constructor, CreateOptions(1)).Dispose();
        }

        [ConditionalTheory(nameof(IsSupported))]
        [InlineData("continue")]
        [InlineData("complete")]
        [InlineData("throw")]
        public void SendZeroCopy_KeepsManagedPinUntilNativeNotification(string decision)
        {
            const byte SendZeroCopyOpcode = 47;
            using (Socket probeSocket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            using (IoRingBoundHandle probeBinding = IoUring.Bind(probeSocket.SafeHandle, ownsFileDescriptor: true))
            {
                if (!probeBinding.IsOperationSupported(SendZeroCopyOpcode))
                {
                    throw new SkipTestException("The kernel does not support IORING_OP_SEND_ZC.");
                }
            }

            RemoteExecutor.Invoke(async decision =>
            {
                (Socket sender, Socket receiver) = SocketTestExtensions.CreateConnectedSocketPair();
                using (sender)
                using (receiver)
                using (TrackingMemoryManager memory = new(2 * 1024 * 1024))
                using (IoRingBoundHandle binding = IoUring.Bind(sender.SafeHandle, ownsFileDescriptor: true))
                {
                    Assert.True(binding.IsOperationSupported(SendZeroCopyOpcode));
                    new Random(42).NextBytes(memory.GetSpan());
                    IoUringRequest request = new(IoUringOperationKind.SendZeroCopy, (ReadOnlyMemory<byte>)memory.Memory);
                    TaskCompletionSource<Exception?> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    InvalidOperationException failure = new("Zero-copy completion failed.");
                    int bytesSent = 0;
                    int lastResult = 0;
                    int dataCompletions = 0;
                    int notifications = 0;
                    bool awaitingNotification = false;
                    CompletionActionOperation operation = new(request, completion =>
                    {
                        Assert.Equal(1, memory.PinCount);
                        Assert.Equal(0, memory.UnpinCount);
                        if (!completion.IsNotification)
                        {
                            Assert.False(awaitingNotification);
                            Assert.InRange(completion.Result, 1, memory.Memory.Length - bytesSent);
                            Assert.True(completion.HasMore);
                            awaitingNotification = true;
                            dataCompletions++;
                            bytesSent += completion.Result;
                            lastResult = completion.Result;
                            return decision switch
                            {
                                "complete" => IoUringCompletionAction.Complete,
                                "throw" => throw failure,
                                _ => IoUringCompletionAction.Continue,
                            };
                        }

                        Assert.True(awaitingNotification);
                        Assert.False(completion.HasMore);
                        Assert.Equal(0, completion.Result);
                        awaitingNotification = false;
                        notifications++;
                        if (bytesSent != memory.Memory.Length)
                        {
                            request = request.SliceBuffer(lastResult, memory.Memory.Length - bytesSent);
                            return IoUringCompletionAction.Resubmit(request);
                        }
                        return IoUringCompletionAction.Complete;
                    }, error => completed.SetResult(error));
#pragma warning disable IL2075 // The RemoteExecutor process is untrimmed.
                    System.Reflection.PropertyInfo nativePending = typeof(IoUringOperation).GetProperty("IsNativePending",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
#pragma warning restore IL2075
                    memory.OnUnpin = () =>
                    {
                        Assert.False((bool)nativePending.GetValue(operation)!);
                        Assert.False(completed.Task.IsCompleted);
                    };
                    byte[] received = new byte[memory.Memory.Length];
                    Task<int> reading = ReadUntilShutdown();
                    binding.EnqueueForSubmission(operation);
                    Exception? error = await completed.Task.WaitAsync(TestSettings.PassingTestTimeout);
                    Assert.Equal(1, memory.PinCount);
                    Assert.Equal(1, memory.UnpinCount);
                    Assert.False((bool)nativePending.GetValue(operation)!);
                    sender.Shutdown(SocketShutdown.Send);
                    int bytesReceived = await reading.WaitAsync(TestSettings.PassingTestTimeout);
                    Assert.Equal(bytesSent, bytesReceived);
                    AssertExtensions.SequenceEqual(memory.GetSpan().Slice(0, bytesSent), received.AsSpan(0, bytesReceived));
                    if (decision == "continue")
                    {
                        Assert.Null(error);
                        Assert.Equal(memory.Memory.Length, bytesSent);
                        Assert.True(dataCompletions > 0);
                        Assert.Equal(dataCompletions, notifications);
                        Assert.False(awaitingNotification);
                    }
                    else
                    {
                        Assert.Equal(1, dataCompletions);
                        Assert.Equal(0, notifications);
                        if (decision == "throw")
                        {
                            Assert.Same(failure, error);
                        }
                        else
                        {
                            Assert.Null(error);
                        }
                    }

                    async Task<int> ReadUntilShutdown()
                    {
                        int total = 0;
                        byte[] chunk = new byte[16384];
                        while (true)
                        {
                            int count = await receiver.ReceiveAsync(chunk.AsMemory());
                            if (count == 0)
                            {
                                return total;
                            }
                            chunk.AsSpan(0, count).CopyTo(received.AsSpan(total));
                            total += count;
                        }
                    }
                }
            }, decision, CreateOptions(1)).Dispose();
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
            private readonly byte[] _buffer;
            public int PinCount;
            public int UnpinCount;
            public int DisposeCount;
            public Action? OnPin;
            public Action? OnUnpin;
            public bool ThrowOnPin;

            public TrackingMemoryManager(int length = 1) => _buffer = new byte[length];

            public override Span<byte> GetSpan() => _buffer;

            public override unsafe MemoryHandle Pin(int elementIndex = 0)
            {
                if (ThrowOnPin)
                {
                    throw new InvalidOperationException("Pin failed.");
                }
                Assert.InRange(elementIndex, 0, _buffer.Length);
                GCHandle handle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
                Interlocked.Increment(ref PinCount);
                MemoryHandle pin = new((byte*)handle.AddrOfPinnedObject() + elementIndex, handle, this);
                try
                {
                    Interlocked.Exchange(ref OnPin, null)?.Invoke();
                    return pin;
                }
                catch
                {
                    pin.Dispose();
                    throw;
                }
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
