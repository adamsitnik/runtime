// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.RemoteExecutor;
using Xunit;

namespace System.Net.Sockets.Tests
{
    public class SocketBlockingModeTransitionTests
    {
        [ConditionalTheory(typeof(IoUringTests), nameof(IoUringTests.IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AcceptAsync_SetsCloseOnExec(bool pending)
        {
            using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            if (!pending)
            {
                client.Connect(listener.LocalEndPoint!);
            }
            Task<Socket> accept = listener.AcceptAsync();
            Assert.Equal(!pending, accept.IsCompleted);
            if (pending)
            {
                client.Connect(listener.LocalEndPoint!);
            }
            using Socket accepted = await accept.WaitAsync(TestSettings.PassingTestTimeout);
            int flags = Interop.Sys.Fcntl.GetFD(accepted.SafeHandle);
            Assert.NotEqual(-1, flags);
            Assert.Equal(1, flags & 1); // FD_CLOEXEC
        }

        [ConditionalFact(typeof(IoUringTests), nameof(IoUringTests.IsSupported))]
        public void ConnectAsync_QueuedSendPreservesNonBlockingMode()
        {
            RemoteInvokeOptions options = new RemoteInvokeOptions();
            options.StartInfo.Environment["DOTNET_USE_IO_URING"] = "1";
            options.StartInfo.Environment["DOTNET_IORING_THREAD_COUNT"] = "1";
            RemoteExecutor.Invoke(() =>
            {
                ThreadPool.GetMinThreads(out _, out int minIo);
                ThreadPool.GetMaxThreads(out _, out int maxIo);
                Assert.True(ThreadPool.SetMinThreads(1, minIo));
                Assert.True(ThreadPool.SetMaxThreads(1, maxIo));
                using ManualResetEventSlim workerStarted = new ManualResetEventSlim();
                using ManualResetEventSlim releaseWorker = new ManualResetEventSlim();
                Task worker = Task.Run(() =>
                {
                    workerStarted.Set();
                    releaseWorker.Wait();
                });
                Assert.True(workerStarted.Wait(TestSettings.PassingTestTimeout));

                using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(1);
                using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    Task connect = client.ConnectAsync(listener.LocalEndPoint!);
                    using Socket accepted = listener.Accept();
                    Assert.False(connect.IsCompleted);
                    Task<int> send = client.SendAsync(new byte[] { 42 }.AsMemory(), SocketFlags.None).AsTask();
                    Assert.False(send.IsCompleted);
                    releaseWorker.Set();
                    Assert.True(connect.Wait(TestSettings.PassingTestTimeout));
                    Assert.True(send.Wait(TestSettings.PassingTestTimeout));
                    Assert.Equal(1, send.Result);
                    Assert.True(IsSocketNonBlocking(client));
                    byte[] buffer = new byte[1];
                    Assert.Equal(1, accepted.Receive(buffer));
                    Assert.Equal(42, buffer[0]);
                }
                finally
                {
                    releaseWorker.Set();
                    Assert.True(worker.Wait(TestSettings.PassingTestTimeout));
                }
            }, options).Dispose();
        }

        private static bool IsSocketNonBlocking(Socket socket)
        {
            int rv = Interop.Sys.Fcntl.GetIsNonBlocking(socket.SafeHandle, out bool isNonBlocking);
            Assert.NotEqual(-1, rv);
            return isNonBlocking;
        }

        [Fact]
        public async Task ConnectAsync_Success_SocketIsBlockingAfterCompletion()
        {
            using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);

            using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            Assert.True(client.Blocking);
            Assert.False(IsSocketNonBlocking(client));

            await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);

            Assert.True(client.Blocking);
            Assert.False(IsSocketNonBlocking(client));
        }

        [Fact]
        public async Task ConnectAsync_UserSetNonBlocking_SocketStaysNonBlocking()
        {
            using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);

            using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            client.Blocking = false;
            Assert.False(client.Blocking);
            Assert.True(IsSocketNonBlocking(client));

            await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);

            Assert.False(client.Blocking);
            Assert.True(IsSocketNonBlocking(client));
        }

        [Fact]
        public async Task ConnectAsync_ThenSendAsync_SocketBecomesNonBlocking()
        {
            using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);

            using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);

            Assert.True(client.Blocking);
            Assert.False(IsSocketNonBlocking(client));

            using Socket accepted = listener.Accept();

            await client.SendAsync(new byte[] { 1, 2, 3 }, SocketFlags.None);

            Assert.True(IsSocketNonBlocking(client));
        }

        [Fact]
        public async Task ConnectAsync_ThenReceiveAsync_SocketBecomesNonBlocking()
        {
            using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);

            using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);

            Assert.True(client.Blocking);
            Assert.False(IsSocketNonBlocking(client));

            using Socket accepted = listener.Accept();
            accepted.Send(new byte[] { 1, 2, 3 });

            byte[] buffer = new byte[10];
            await client.ReceiveAsync(buffer, SocketFlags.None);

            Assert.True(IsSocketNonBlocking(client));
        }

        [Fact]
        public async Task ConnectAsync_Failure_SocketIsRestoredToBlocking()
        {
            using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            await Assert.ThrowsAsync<SocketException>(async () =>
                await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 1)));

            Assert.False(IsSocketNonBlocking(client));
        }

        [Fact]
        public async Task ConnectAsync_WithBuffer_Failure_CallbackInvokedAndSocketIsBlocking()
        {
            using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            using var saea = new SocketAsyncEventArgs();
            saea.RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 1);
            saea.SetBuffer(new byte[] { 1, 2, 3 }, 0, 3);

            var tcs = new TaskCompletionSource();
            saea.Completed += (_, _) => tcs.SetResult();

            if (!client.ConnectAsync(saea))
            {
                tcs.SetResult();
            }

            await tcs.Task;

            Assert.NotEqual(SocketError.Success, saea.SocketError);
            Assert.False(IsSocketNonBlocking(client));
        }

        [Fact]
        public async Task AcceptAsync_AcceptedSocketIsBlockingByDefault()
        {
            using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);

            using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect((IPEndPoint)listener.LocalEndPoint!);

            using Socket accepted = await listener.AcceptAsync();

            Assert.True(accepted.Blocking);
            Assert.False(IsSocketNonBlocking(accepted));
        }

        [Fact]
        public async Task AcceptAsync_AcceptedSocketSyncReceiveWorks()
        {
            using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);

            using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect((IPEndPoint)listener.LocalEndPoint!);

            using Socket accepted = await listener.AcceptAsync();

            client.Send(new byte[] { 1, 2, 3 });

            byte[] buffer = new byte[10];
            int received = accepted.Receive(buffer);

            Assert.Equal(3, received);
            Assert.True(accepted.Blocking);
            Assert.False(IsSocketNonBlocking(accepted));
        }

        [Fact]
        public async Task AcceptAsync_ConcurrentAccepts_DoNotCorruptListenerState()
        {
            using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(5);

            Task<Socket> accept1 = listener.AcceptAsync();
            Task<Socket> accept2 = listener.AcceptAsync();

            using Socket client1 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using Socket client2 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client1.Connect((IPEndPoint)listener.LocalEndPoint!);
            client2.Connect((IPEndPoint)listener.LocalEndPoint!);

            using Socket accepted1 = await accept1;
            using Socket accepted2 = await accept2;

            Assert.True(accepted1.Blocking);
            Assert.False(IsSocketNonBlocking(accepted1));
            Assert.True(accepted2.Blocking);
            Assert.False(IsSocketNonBlocking(accepted2));
        }

        [ActiveIssue("https://github.com/dotnet/runtime/issues/128141", TestPlatforms.Android)]
        [Fact]
        public async Task ConnectAsync_WithBuffer_Succeeds()
        {
            using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);

            using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            using var saea = new SocketAsyncEventArgs();
            saea.RemoteEndPoint = (IPEndPoint)listener.LocalEndPoint!;
            saea.SetBuffer(new byte[] { 1, 2, 3 }, 0, 3);

            var tcs = new TaskCompletionSource();
            saea.Completed += (_, _) => tcs.SetResult();

            bool completedAsync = client.ConnectAsync(saea);
            if (!completedAsync)
            {
                tcs.SetResult();
            }

            await tcs.Task;

            Assert.Equal(SocketError.Success, saea.SocketError);
            Assert.True(client.Blocking);

            // On Apple and Android platforms, TFO (connectx/sendto) may complete the connect+send
            // in a single syscall, so the socket can end up blocking even on the async path.
            // On Linux, async connect always leaves the socket non-blocking when
            // buffer > 0 because SendToAsync is pending.
            if (!completedAsync || PlatformDetection.IsApplePlatform || PlatformDetection.IsAndroid)
            {
                Assert.False(IsSocketNonBlocking(client));
            }
            else
            {
                Assert.True(IsSocketNonBlocking(client));
            }
        }
    }
}
