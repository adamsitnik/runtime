// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading;

namespace System.Net.Sockets;

public abstract partial class IoUringAcceptOperation
{
    /// <inheritdoc/>
    protected sealed override IoUringRequest PrepareRequest() => throw new PlatformNotSupportedException();

    /// <inheritdoc/>
    protected sealed override IoUringCompletionAction ProcessCompletion(in IoUringCompletion completion) =>
        throw new PlatformNotSupportedException();
}
