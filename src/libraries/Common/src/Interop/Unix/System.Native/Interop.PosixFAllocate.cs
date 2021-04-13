// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static partial class Interop
{
    internal static partial class Sys
    {
        [DllImport(Libraries.SystemNative, EntryPoint = "SystemNative_PosixFAllocate", SetLastError = false /* this is explicitly called out in the man page */)]
        internal static extern int PosixFAllocate(SafeFileHandle fd, long offset, long length);
    }
}
