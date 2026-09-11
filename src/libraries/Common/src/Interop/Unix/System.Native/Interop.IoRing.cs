// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;

internal static partial class Interop
{
    internal static partial class Sys
    {
        // Mirrors the native IoRingOp enum in pal_io.h.
        internal enum IoRingOp : int
        {
            Read = 0,
            Write = 1,
            ReadV = 2,
            WriteV = 3,
        }

        // Mirrors the native IoRingRequest struct in pal_io.h.
        // Fd is a raw file descriptor (not a SafeHandle): the caller is responsible for keeping
        // the owning SafeHandle ref-counted/alive for as long as the request may be in flight.
        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct IoRingRequest
        {
            public IoRingOp OpCode;
            public IntPtr Fd;
            public long Offset; // -1 for non-positional ops
            public byte* Buffer; // used by Read/Write
            public int BufferLength;
            public IOVector* Vectors; // used by ReadV/WriteV
            public int VectorCount;
            public ulong UserData;
        }

        // Mirrors the native IoRingCompletion struct in pal_io.h.
        [StructLayout(LayoutKind.Sequential)]
        internal struct IoRingCompletion
        {
            public ulong UserData;
            public int Result;
            public uint Flags;
        }

        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_IoRingIsAvailable")]
        internal static partial int IoRingIsAvailable();

        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_IoRingCreate", SetLastError = true)]
        internal static partial int IoRingCreate(int submissionQueueDepth, int completionQueueDepth, out IntPtr ringHandle);

        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_IoRingSubmit", SetLastError = true)]
        internal static unsafe partial int IoRingSubmit(IntPtr ringHandle, IoRingRequest* requests, int requestCount, out int submittedCount);

        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_IoRingKick", SetLastError = true)]
        internal static partial int IoRingKick(IntPtr ringHandle);

        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_IoRingWaitForCompletions", SetLastError = true)]
        internal static unsafe partial int IoRingWaitForCompletions(IntPtr ringHandle, IoRingCompletion* completions, int maxCompletions, int minComplete, out int completedCount);

        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_IoRingClose", SetLastError = true)]
        internal static partial int IoRingClose(IntPtr ringHandle);
    }
}
