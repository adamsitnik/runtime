// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading
{
    internal sealed partial class PortableThreadPool
    {
        private static partial class WorkerThread
        {
            // Prevents this thread from exiting (and abandoning its own io_uring ring - there is no
            // work stealing in the per-thread-ring architecture, see IoUringThreadPool) while it still
            // has operations in flight on that ring; their completions would otherwise never be
            // processed.
            private static bool IsIOPending => IoUringThreadPool.CurrentThreadHasPendingOperations;
        }

        private struct CpuUtilizationReader
        {
            private Interop.Sys.ProcessCpuInformation _cpuInfo;

            public double CurrentUtilization =>
                Interop.Sys.GetCpuUtilization(ref _cpuInfo) / Environment.ProcessorCount;
        }
    }
}
