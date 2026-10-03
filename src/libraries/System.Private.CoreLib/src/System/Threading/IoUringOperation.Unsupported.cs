// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading;

public abstract partial class IoUringOperation
{
    private bool IsCancellationRequestedCore => throw new PlatformNotSupportedException();

    private void RequestCancellationCore() => throw new PlatformNotSupportedException();

    private System.Runtime.InteropServices.SafeHandle TakeAcceptedHandleCore() => throw new PlatformNotSupportedException();

    private void ExecuteCore() => throw new PlatformNotSupportedException();
}
