// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using System.Threading;

namespace System.Net.Sockets;

/// <summary>Accepts an ordered sequence of socket handles using a multishot io_uring request.</summary>
/// <remarks>
/// Each accepted handle transfers to <see cref="IoUringMultishotOperation{T}.OnNext"/> at callback entry.
/// The implementation must dispose the handle when finished with it, even if the callback throws.
/// Callback exceptions stop acceptance and are reported after outstanding native completions drain.
/// </remarks>
[CLSCompliant(false)]
[SupportedOSPlatform("linux")]
public abstract partial class IoUringAcceptOperation : IoUringMultishotOperation<SafeSocketHandle>
{
    /// <summary>Initializes a new instance of the <see cref="IoUringAcceptOperation"/> class.</summary>
    protected IoUringAcceptOperation()
    {
    }
}
