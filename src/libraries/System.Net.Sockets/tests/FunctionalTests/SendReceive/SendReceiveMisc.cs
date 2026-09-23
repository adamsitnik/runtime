// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace System.Net.Sockets.Tests
{
    public class ReceiveMultishot
    {
        private static (Socket Sender, Socket Receiver) CreatePair()
        {
            using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            Socket sender = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            sender.Connect(listener.LocalEndPoint!);
            Socket receiver = listener.Accept();
            return (sender, receiver);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(8 * 1024 * 1024 + 171)]
        public async Task BytesRemainOrderedAcrossBufferReuseAndEof(int length)
        {
            (Socket sender, Socket receiver) = CreatePair();
            using (sender)
            using (receiver)
            {
                byte[] payload = new byte[length];
                for (int i = 0; i < payload.Length; i++)
                {
                    payload[i] = (byte)(i % 251);
                }
                Task send = Task.Run(() =>
                {
                    int offset = 0;
                    while (offset < payload.Length)
                    {
                        offset += sender.Send(payload.AsSpan(offset, Math.Min(65536, payload.Length - offset)));
                    }
                    sender.Shutdown(SocketShutdown.Send);
                });
                int received = 0;
                await using IAsyncEnumerator<IMemoryOwner<byte>> enumerator = receiver.ReceiveMultishotAsync().GetAsyncEnumerator();
                while (await enumerator.MoveNextAsync().AsTask().WaitAsync(TestSettings.PassingTestTimeout))
                {
                    using IMemoryOwner<byte> owner = enumerator.Current;
                    Assert.InRange(owner.Memory.Length, 1, payload.Length - received);
                    Assert.True(owner.Memory.Span.SequenceEqual(payload.AsSpan(received, owner.Memory.Length)));
                    received += owner.Memory.Length;
                }
                await send.WaitAsync(TestSettings.PassingTestTimeout);
                Assert.Equal(length, received);
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task CancellationPreservesTokenAndSocket(bool enumeratorToken, bool precanceled)
        {
            (Socket sender, Socket receiver) = CreatePair();
            using (sender)
            using (receiver)
            using (CancellationTokenSource cancellation = new())
            {
                if (precanceled)
                {
                    cancellation.Cancel();
                }
                await using (IAsyncEnumerator<IMemoryOwner<byte>> enumerator = receiver.ReceiveMultishotAsync(
                    enumeratorToken ? default : cancellation.Token).GetAsyncEnumerator(enumeratorToken ? cancellation.Token : default))
                {
                    Task<bool> pending = enumerator.MoveNextAsync().AsTask();
                    cancellation.Cancel();
                    OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => pending.WaitAsync(TestSettings.PassingTestTimeout));
                    Assert.Equal(cancellation.Token, exception.CancellationToken);
                }
                sender.Send(new byte[] { 42 });
                await using IAsyncEnumerator<IMemoryOwner<byte>> next = receiver.ReceiveMultishotAsync().GetAsyncEnumerator();
                Assert.True(await next.MoveNextAsync().AsTask().WaitAsync(TestSettings.PassingTestTimeout));
                using IMemoryOwner<byte> owner = next.Current;
                Assert.Equal(new byte[] { 42 }, owner.Memory.ToArray());
            }
        }

        [Fact]
        public async Task SocketDisposalCompletesPendingReceive()
        {
            (Socket sender, Socket receiver) = CreatePair();
            using (sender)
            using (receiver)
            {
                await using IAsyncEnumerator<IMemoryOwner<byte>> enumerator = receiver.ReceiveMultishotAsync().GetAsyncEnumerator();
                Task<bool> pending = enumerator.MoveNextAsync().AsTask();
                Assert.False(pending.IsCompleted);
                await Task.Run(receiver.Dispose).WaitAsync(TestSettings.PassingTestTimeout);
                SocketException exception = await Assert.ThrowsAsync<SocketException>(() => pending.WaitAsync(TestSettings.PassingTestTimeout));
                Assert.Equal(SocketError.OperationAborted, exception.SocketErrorCode);
            }
        }

        [Fact]
        public async Task YieldedLeaseSurvivesEnumeratorAndSocketDisposal()
        {
            (Socket sender, Socket receiver) = CreatePair();
            using (sender)
            using (receiver)
            {
                IAsyncEnumerator<IMemoryOwner<byte>> enumerator = receiver.ReceiveMultishotAsync().GetAsyncEnumerator();
                Task<bool> pending = enumerator.MoveNextAsync().AsTask();
                sender.Send(new byte[] { 1, 2, 3 });
                Assert.True(await pending.WaitAsync(TestSettings.PassingTestTimeout));
                IMemoryOwner<byte> owner = enumerator.Current;
                try
                {
                    await enumerator.DisposeAsync().AsTask().WaitAsync(TestSettings.PassingTestTimeout);
                    receiver.Dispose();
                    Assert.Equal(new byte[] { 1, 2, 3 }, owner.Memory.ToArray());
                }
                finally
                {
                    owner.Dispose();
                    owner.Dispose();
                }
                Assert.Throws<ObjectDisposedException>(() => owner.Memory);
            }
        }

        [Fact]
        public async Task ConcurrentEnumerationIsRejected()
        {
            (Socket sender, Socket receiver) = CreatePair();
            using (sender)
            using (receiver)
            using (CancellationTokenSource cancellation = new())
            {
                await using IAsyncEnumerator<IMemoryOwner<byte>> first = receiver.ReceiveMultishotAsync(cancellation.Token).GetAsyncEnumerator();
                Task<bool> pending = first.MoveNextAsync().AsTask();
                await using IAsyncEnumerator<IMemoryOwner<byte>> second = receiver.ReceiveMultishotAsync().GetAsyncEnumerator();
                await Assert.ThrowsAsync<InvalidOperationException>(() => second.MoveNextAsync().AsTask());
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestSettings.PassingTestTimeout));
            }
        }

        [Fact]
        public void InvalidSocketIsRejected()
        {
            using Socket datagram = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            SocketException exception = Assert.Throws<SocketException>(() => datagram.ReceiveMultishotAsync());
            Assert.Equal(SocketError.OperationNotSupported, exception.SocketErrorCode);
            datagram.Dispose();
            Assert.Throws<ObjectDisposedException>(() => datagram.ReceiveMultishotAsync());
        }

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsNotWindows))]
        public async Task NonOwningSocketDisposalPreservesDescriptor()
        {
            (Socket sender, Socket receiver) = CreatePair();
            using (sender)
            using (receiver)
            using (Socket borrowed = new(new SafeSocketHandle(receiver.SafeHandle.DangerousGetHandle(), ownsHandle: false)))
            {
                await using IAsyncEnumerator<IMemoryOwner<byte>> enumerator = borrowed.ReceiveMultishotAsync().GetAsyncEnumerator();
                Task<bool> pending = enumerator.MoveNextAsync().AsTask();
                await Task.Run(borrowed.Dispose).WaitAsync(TestSettings.PassingTestTimeout);
                SocketException exception = await Assert.ThrowsAsync<SocketException>(() => pending.WaitAsync(TestSettings.PassingTestTimeout));
                Assert.Equal(SocketError.OperationAborted, exception.SocketErrorCode);
                Assert.Equal(1, sender.Send(new byte[] { 57 }));
                byte[] received = new byte[1];
                Assert.Equal(1, receiver.Receive(received));
                Assert.Equal(57, received[0]);
            }
        }

        [Fact]
        public async Task ResetUpdatesConnectedState()
        {
            (Socket sender, Socket receiver) = CreatePair();
            using (sender)
            using (receiver)
            {
                await using IAsyncEnumerator<IMemoryOwner<byte>> enumerator = receiver.ReceiveMultishotAsync().GetAsyncEnumerator();
                Task<bool> pending = enumerator.MoveNextAsync().AsTask();
                sender.LingerState = new LingerOption(true, 0);
                sender.Dispose();
                SocketException exception = await Assert.ThrowsAsync<SocketException>(() => pending.WaitAsync(TestSettings.PassingTestTimeout));
                Assert.Equal(SocketError.ConnectionReset, exception.SocketErrorCode);
                Assert.False(receiver.Connected);
            }
        }
    }

    public class SendReceiveMisc
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SendAsyncBackpressureIncludesSynchronousPrefixAndCompletesRemainder(bool cancellable)
        {
            using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            using Socket sender = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            sender.Connect(listener.LocalEndPoint!);
            using Socket receiver = listener.Accept();
            sender.SendBufferSize = 4096;
            byte[] storage = new byte[4 * 1024 * 1024 + 150];
            for (int i = 0; i < storage.Length; i++)
            {
                storage[i] = (byte)(i % 251);
            }
            Memory<byte> payload = storage.AsMemory(37);
            using CancellationTokenSource cancellation = new();
            Task<int> sending = sender.SendAsync(payload, SocketFlags.None, cancellable ? cancellation.Token : default).AsTask();
            Assert.False(sending.IsCompleted);
            Task<byte[]> receiving = Task.Run(() =>
            {
                using System.IO.MemoryStream received = new();
                byte[] buffer = new byte[65536];
                int count;
                while ((count = receiver.Receive(buffer)) != 0)
                {
                    received.Write(buffer, 0, count);
                }
                return received.ToArray();
            });
            int sent = await sending.WaitAsync(TestSettings.PassingTestTimeout);
            sender.Shutdown(SocketShutdown.Send);
            byte[] actual = await receiving.WaitAsync(TestSettings.PassingTestTimeout);
            Assert.Equal(payload.Length, sent);
            Assert.True(payload.Span.SequenceEqual(actual));
        }

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsMultithreadingSupported))]
        public void SendRecvIovMaxTcp_Success()
        {
            // sending/receiving more than IOV_MAX segments causes EMSGSIZE on some platforms.
            // This is handled internally for stream sockets so this error shouldn't surface.

            // Use more than IOV_MAX (1024 on Linux & macOS) segments.
            const int SegmentCount = 2400;
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                server.BindToAnonymousPort(IPAddress.Loopback);
                server.Listen(1);

                var sendBuffer = new byte[SegmentCount];
                Task serverProcessingTask = Task.Run(() =>
                {
                    using (Socket acceptSocket = server.Accept())
                    {
                        // send data as SegmentCount (> IOV_MAX) 1-byte segments.
                        var sendSegments = new List<ArraySegment<byte>>();
                        for (int i = 0; i < SegmentCount; i++)
                        {
                            sendBuffer[i] = (byte)i;
                            sendSegments.Add(new ArraySegment<byte>(sendBuffer, i, 1));
                        }
                        SocketError error;
                        // Send blocks until all segments are sent.
                        int bytesSent = acceptSocket.Send(sendSegments, SocketFlags.None, out error);

                        Assert.Equal(SegmentCount, bytesSent);
                        Assert.Equal(SocketError.Success, error);

                        acceptSocket.Shutdown(SocketShutdown.Send);
                    }
                });

                using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    client.Connect(server.LocalEndPoint);

                    // receive data as 1-byte segments.
                    var receiveBuffer = new byte[SegmentCount];
                    var receiveSegments = new List<ArraySegment<byte>>();
                    for (int i = 0; i < SegmentCount; i++)
                    {
                        receiveSegments.Add(new ArraySegment<byte>(receiveBuffer, i, 1));
                    }
                    var bytesReceivedTotal = 0;
                    do
                    {
                        SocketError error;
                        // Receive can return up to IOV_MAX segments.
                        int bytesReceived = client.Receive(receiveSegments, SocketFlags.None, out error);
                        bytesReceivedTotal += bytesReceived;
                        // Offset receiveSegments for next Receive.
                        receiveSegments.RemoveRange(0, bytesReceived);

                        Assert.NotEqual(0, bytesReceived);
                        Assert.Equal(SocketError.Success, error);
                    } while (bytesReceivedTotal != SegmentCount);

                    AssertExtensions.Equal(sendBuffer, receiveBuffer);
                }
            }
        }

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsMultithreadingSupported))]
        public void SendIovMaxUdp_SuccessOrMessageSize()
        {
            // sending more than IOV_MAX segments causes EMSGSIZE on some platforms.
            // We handle this for stream sockets by truncating.
            // This test verifies we are not truncating non-stream sockets.

            // Use more than IOV_MAX (1024 on Linux & macOS) segments
            // and less than Ethernet MTU.
            const int SegmentCount = 1200;
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.BindToAnonymousPort(IPAddress.Loopback);
                // Use our own address as destination.
                socket.Connect(socket.LocalEndPoint);

                var sendBuffer = new byte[SegmentCount];
                var sendSegments = new List<ArraySegment<byte>>();
                for (int i = 0; i < SegmentCount; i++)
                {
                    sendBuffer[i] = (byte)i;
                    sendSegments.Add(new ArraySegment<byte>(sendBuffer, i, 1));
                }

                SocketError error;
                // send data as SegmentCount (> IOV_MAX) 1-byte segments.
                int bytesSent = socket.Send(sendSegments, SocketFlags.None, out error);
                if (error == SocketError.Success)
                {
                    // platform sent message with > IOV_MAX segments
                    Assert.Equal(SegmentCount, bytesSent);
                }
                else
                {
                    // platform returns EMSGSIZE
                    Assert.Equal(SocketError.MessageSize, error);
                    Assert.Equal(0, bytesSent);
                }
            }
        }

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsMultithreadingSupported))]
        public async Task ReceiveIovMaxUdp_SuccessOrMessageSize()
        {
            // receiving more than IOV_MAX segments causes EMSGSIZE on some platforms.
            // We handle this for stream sockets by truncating.
            // This test verifies we are not truncating non-stream sockets.

            // Use more than IOV_MAX (1024 on Linux & macOS) segments
            // and less than Ethernet MTU.
            const int SegmentCount = 1200;
            var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sender.BindToAnonymousPort(IPAddress.Loopback);
            var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            receiver.Connect(sender.LocalEndPoint); // only receive from sender
            EndPoint receiverEndPoint = receiver.LocalEndPoint;

            Barrier b = new Barrier(2);

            Task receiveTask = Task.Run(() =>
            {
                using (receiver)
                {
                    var receiveBuffer = new byte[SegmentCount];
                    var receiveSegments = new List<ArraySegment<byte>>();
                    for (int i = 0; i < SegmentCount; i++)
                    {
                        receiveSegments.Add(new ArraySegment<byte>(receiveBuffer, i, 1));
                    }
                    // receive data as SegmentCount (> IOV_MAX) 1-byte segments.
                    SocketError error;
                    // Signal we are ready to receive.
                    b.SignalAndWait();
                    int bytesReceived = receiver.Receive(receiveSegments, SocketFlags.None, out error);

                    if (error == SocketError.Success)
                    {
                        // platform received message in > IOV_MAX segments
                        Assert.Equal(SegmentCount, bytesReceived);
                    }
                    else
                    {
                        // platform returns EMSGSIZE
                        Assert.Equal(SocketError.MessageSize, error);
                        Assert.Equal(0, bytesReceived);
                    }
                }
            });

            using (sender)
            {
                sender.Connect(receiverEndPoint);

                // Synchronize and wait for receiving task to be ready.
                b.SignalAndWait();

                var sendBuffer = new byte[SegmentCount];
                for (int i = 0; i < 10; i++) // UDPRedundancy
                {
                    int bytesSent = sender.Send(sendBuffer);
                    Assert.Equal(SegmentCount, bytesSent);
                    try
                    {
                        await receiveTask.WaitAsync(TimeSpan.FromMilliseconds(3));
                        break;
                    }
                    catch (TimeoutException) { }
                }
            }

            Assert.True(receiveTask.IsCompleted);
            await receiveTask;
        }

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsMultithreadingSupported))]
        [SkipOnPlatform(TestPlatforms.Windows, "All data is sent, even when very large (100M).")]
        public void SocketSendWouldBlock_ReturnsBytesSent()
        {
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                // listen
                server.BindToAnonymousPort(IPAddress.Loopback);
                server.Listen(1);
                // connect
                client.Connect(server.LocalEndPoint);
                // accept
                using (Socket socket = server.Accept())
                {
                    // We send a large amount of data but don't read it.
                    // A chunck will be sent, attempts to send more will return SocketError.WouldBlock.
                    // Socket.Send must return the success of the partial send.
                    socket.Blocking = false;
                    var data = new byte[5_000_000];
                    SocketError error;
                    int bytesSent = socket.Send(data, 0, data.Length, SocketFlags.None, out error);

                    Assert.Equal(SocketError.Success, error);
                    Assert.InRange(bytesSent, 1, data.Length - 1);
                }
            }
        }

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsMultithreadingSupported))]
        [PlatformSpecific(TestPlatforms.AnyUnix)]
        public async Task Socket_ReceiveFlags_Success()
        {
            using (var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            using (var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                receiver.BindToAnonymousPort(IPAddress.Loopback);
                sender.Connect(receiver.LocalEndPoint);
                sender.SendBufferSize = 1500;

                var data = new byte[500];
                data[0] = data[499] = 1;

                Assert.Equal(500, sender.Send(data));
                data[0] = data[499] = 2;
                Assert.Equal(500, sender.Send(data));

                var tcs = new TaskCompletionSource();
                SocketAsyncEventArgs args = new SocketAsyncEventArgs();

                var receiveBuffer = new byte[600];
                receiveBuffer[0] = data[499] = 0;

                args.SetBuffer(receiveBuffer, 0, receiveBuffer.Length);
                args.Completed += delegate { tcs.SetResult(); };

                // First peek at the message.
                args.SocketFlags = SocketFlags.Peek;
                if (receiver.ReceiveAsync(args))
                {
                    await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(TestSettings.PassingTestTimeout));
                }
                Assert.Equal(SocketFlags.None, args.SocketFlags);
                Assert.Equal(1, receiveBuffer[0]);
                Assert.Equal(1, receiveBuffer[499]);
                receiveBuffer[0] = receiveBuffer[499] = 0;

                // Now, we should be able to get same message again.
                tcs = new TaskCompletionSource();
                args.SocketFlags = SocketFlags.None;
                if (receiver.ReceiveAsync(args))
                {
                    await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(TestSettings.PassingTestTimeout));
                }
                Assert.Equal(SocketFlags.None, args.SocketFlags);
                Assert.Equal(1, receiveBuffer[0]);
                Assert.Equal(1, receiveBuffer[499]);
                receiveBuffer[0] = receiveBuffer[499] = 0;

                // Set buffer smaller than message.
                tcs = new TaskCompletionSource();
                args.SetBuffer(receiveBuffer, 0, 100);
                if (receiver.ReceiveAsync(args))
                {
                    await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(TestSettings.PassingTestTimeout));
                }
                Assert.Equal(SocketFlags.Truncated, args.SocketFlags);
                Assert.Equal(2, receiveBuffer[0]);

                // There should be no more data.
                Assert.Equal(0, receiver.Available);
            }
        }

        [ConditionalTheory(typeof(PlatformDetection), nameof(PlatformDetection.IsMultithreadingSupported))]
        [InlineData(true)]
        [InlineData(false)]
        public void ReceiveFrom_MultipleRounds_Success(bool async)
        {
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.ReceiveTimeout = 100;
                socket.BindToAnonymousPort(IPAddress.Loopback);

                var address = new SocketAddress(AddressFamily.InterNetwork);
                var buffer = new byte[100];
                int receivedLength;

                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        if (async)
                        {
                            using var cts = new CancellationTokenSource();
                            cts.CancelAfter(100);
                            receivedLength = socket.ReceiveFromAsync(buffer, SocketFlags.None, address, cts.Token).AsTask().GetAwaiter().GetResult();
                        }
                        else
                        {
                            receivedLength = socket.ReceiveFrom(buffer, SocketFlags.None, address);
                        }
                        Assert.Equal(0, receivedLength);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) { }
                    catch (OperationCanceledException) { }
                }
            }
        }
    }
}
