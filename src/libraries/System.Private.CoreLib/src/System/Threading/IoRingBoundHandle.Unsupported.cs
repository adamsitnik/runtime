// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading;

public sealed partial class IoRingBoundHandle
{
    private IoRingBoundHandle() => throw new PlatformNotSupportedException();

    private void EnqueueForSubmissionCore(IoUringOperation operation, CancellationToken cancellationToken) =>
        throw new PlatformNotSupportedException();

    private void DisposeCore()
    {
    }

    private bool DisposeAndWaitCore() => throw new PlatformNotSupportedException();

    private void ExecuteCore() => throw new PlatformNotSupportedException();
}
