// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace System.Net.Sockets
{
    public partial class Socket
    {
        /// <summary>
        /// Not supported on this platform - multishot io_uring receives are only available on Linux.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        public IAsyncEnumerable<IMemoryOwner<byte>> ReceiveMultishotAsync(CancellationToken cancellationToken = default) =>
            throw new PlatformNotSupportedException();
    }
}
