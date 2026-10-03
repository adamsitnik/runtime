// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Win32.SafeHandles;

internal sealed partial class SafeRingHandle : SafeHandle
{
    public SafeRingHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        if (IoRingClose(handle) != 0)
        {
            Environment.FailFast($"io_uring ring cleanup failed: {Marshal.GetLastPInvokeError()}.");
        }
        return true;
    }

    [LibraryImport(Interop.Libraries.SystemNative, EntryPoint = "SystemNative_IoRingClose", SetLastError = true)]
    private static partial int IoRingClose(IntPtr ringHandle);
}
