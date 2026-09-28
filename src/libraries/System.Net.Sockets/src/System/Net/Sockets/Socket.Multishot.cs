// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Sockets
{
    public partial class Socket
    {
        /// <summary>Delivers multishot receive buffers directly to a callback on a Thread Pool worker.</summary>
        /// <param name="onReceived">The callback that takes ownership of each buffer.</param>
        /// <param name="cancellationToken">The token that requests cancellation of the receive operation.</param>
        /// <returns>A task that completes after the receive operation has stopped delivering callbacks.</returns>
        /// <exception cref="PlatformNotSupportedException">The platform does not support io_uring.</exception>
        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        public Task ReceiveMultishotAsync(Action<IMemoryOwner<byte>> onReceived, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(onReceived);
            throw new PlatformNotSupportedException();
        }

        /// <summary>
        /// Not supported on this platform - multishot io_uring receives are only available on Linux.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        public IAsyncEnumerable<IMemoryOwner<byte>> ReceiveMultishotAsync(CancellationToken cancellationToken = default) =>
            throw new PlatformNotSupportedException();
    }
}
