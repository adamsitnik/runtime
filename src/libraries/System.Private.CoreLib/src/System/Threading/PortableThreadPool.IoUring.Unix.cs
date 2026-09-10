// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace System.Threading
{
    internal sealed partial class PortableThreadPool
    {
        /// <summary>
        /// Implements the "Option 3" io_uring/ThreadPool integration described in the io_uring design doc:
        /// a single, plain (non-<c>SINGLE_ISSUER</c>) ring is shared by every Thread Pool worker thread.
        /// Submission is guarded by an ordinary lock. The role of "the thread currently reaping
        /// completions" rotates: any worker thread that is about to park first attempts a non-blocking
        /// CAS on a single "driver" slot. The thread that wins blocks in <c>io_uring_enter</c> waiting for
        /// at least one completion, then (without running any continuation inline) queues the
        /// corresponding continuations as ordinary Thread Pool work items.
        /// </summary>
        internal static class IoUringThreadPool
        {
            // Depth of the shared submission/completion queues. Not currently configurable; may become
            // adaptive (or sharded across multiple rings) in a future iteration.
            private const int QueueDepth = 1024;

            // Opt-out switch: io_uring integration is used by default on Linux when the kernel supports
            // it. Set DOTNET_USE_IO_URING=0 to fall back to the pre-existing (blocking-call-on-a-
            // ThreadPool-work-item) implementation unconditionally.
            private static readonly bool s_isEnabled;

            // The shared ring handle, or IntPtr.Zero if unavailable/disabled. Set at most once.
            private static readonly IntPtr s_ringHandle;

            // Guards (a) pushing SQEs + the non-blocking submit-only io_uring_enter call, and
            // (b) draining currently-available CQEs after a driver wakes up. Never held across the
            // blocking io_uring_enter(min_complete: 1) wait itself.
            private static readonly Lock s_lock = new Lock();

            // CAS slot: 0 == no one is currently driving completions, 1 == a driver is active.
            private static int s_isDriving;

            // Number of io_uring operations submitted but not yet completed. Used to avoid a thread
            // blocking forever in io_uring_enter when there is nothing outstanding to wait for.
            private static int s_inFlightCount;

            static IoUringThreadPool()
            {
                (s_isEnabled, s_ringHandle) = DetermineIsEnabledAndCreateRing();
            }

            /// <summary>Whether the io_uring Thread Pool integration is enabled and usable on this system.</summary>
            public static bool IsEnabled => s_isEnabled;

            private static (bool IsEnabled, IntPtr RingHandle) DetermineIsEnabledAndCreateRing()
            {
                if (!OperatingSystem.IsLinux())
                {
                    return (false, IntPtr.Zero);
                }

                bool configuredOn =
                    AppContextConfigHelper.GetBooleanConfig("System.Threading.ThreadPool.UseIoUring", "DOTNET_USE_IO_URING", defaultValue: true);
                if (!configuredOn)
                {
                    return (false, IntPtr.Zero);
                }

                if (Interop.Sys.IoRingIsAvailable() == 0)
                {
                    return (false, IntPtr.Zero);
                }

                int result = Interop.Sys.IoRingCreate(QueueDepth, QueueDepth, out IntPtr ringHandle);
                if (result != 0)
                {
                    return (false, IntPtr.Zero);
                }

                return (true, ringHandle);
            }

            /// <summary>
            /// Attempts to submit a single request to the shared ring. On success, the operation is now
            /// in flight and its completion will eventually be delivered via
            /// <see cref="IIoUringOperation.CompleteFromIoUring(int)"/>, invoked on a Thread Pool work item.
            /// Returns false if the request could not be submitted (e.g., the submission queue is
            /// currently full); callers should fall back to their non-io_uring code path in that case, as
            /// no partial state is left behind.
            /// </summary>
            public static unsafe bool TrySubmit(IIoUringOperation operation, in Interop.Sys.IoRingRequest request)
            {
                Debug.Assert(s_isEnabled);

                GCHandle handle = GCHandle.Alloc(operation);
                Interop.Sys.IoRingRequest localRequest = request;
                localRequest.UserData = (ulong)GCHandle.ToIntPtr(handle);

                bool submitted;
                using (s_lock.EnterScope())
                {
                    int result = Interop.Sys.IoRingSubmit(s_ringHandle, &localRequest, 1, out int submittedCount);
                    submitted = result == 0 && submittedCount == 1;
                }

                if (submitted)
                {
                    Interlocked.Increment(ref s_inFlightCount);
                }
                else
                {
                    handle.Free();
                }

                return submitted;
            }

            /// <summary>
            /// Called by a worker thread that is about to park (has no work left). If this thread wins
            /// the CAS to become the driver and there is at least one in-flight operation, it blocks
            /// in-kernel waiting for at least one completion, then dispatches the corresponding
            /// continuations as ordinary Thread Pool work items (never inline) before returning.
            /// Returns true if this thread drove (and should re-check for normal work before parking),
            /// false if it should proceed to park normally.
            /// </summary>
            public static unsafe bool TryBecomeDriverAndDrive()
            {
                if (!s_isEnabled)
                {
                    return false;
                }

                if (Volatile.Read(ref s_inFlightCount) == 0)
                {
                    // Nothing to wait for; avoid parking forever in io_uring_enter.
                    return false;
                }

                if (Interlocked.CompareExchange(ref s_isDriving, 1, 0) != 0)
                {
                    // Someone else is already driving.
                    return false;
                }

                try
                {
                    // Block in-kernel, outside of the lock, until at least one completion is available.
                    Interop.Sys.IoRingCompletion completion = default;
                    int result = Interop.Sys.IoRingWaitForCompletions(s_ringHandle, &completion, 1, minComplete: 1, out int completedCount);
                    if (result != 0 || completedCount == 0)
                    {
                        return true;
                    }

                    Dispatch(completion);

                    // Drain any additional completions that are already available without waiting again.
                    while (true)
                    {
                        Interop.Sys.IoRingCompletion nextCompletion = default;
                        int nextResult;
                        using (s_lock.EnterScope())
                        {
                            nextResult = Interop.Sys.IoRingWaitForCompletions(s_ringHandle, &nextCompletion, 1, minComplete: 0, out int nextCompletedCount);
                            if (nextResult != 0 || nextCompletedCount == 0)
                            {
                                break;
                            }
                        }

                        Dispatch(nextCompletion);
                    }
                }
                finally
                {
                    Volatile.Write(ref s_isDriving, 0);
                }

                return true;
            }

            private static void Dispatch(in Interop.Sys.IoRingCompletion completion)
            {
                Interlocked.Decrement(ref s_inFlightCount);

                GCHandle handle = GCHandle.FromIntPtr((IntPtr)completion.UserData);
                var operation = (IIoUringOperation)handle.Target!;
                handle.Free();

                // The driver must not run the continuation inline; CompleteFromIoUring is responsible
                // for queuing the actual continuation as an ordinary Thread Pool work item, which also
                // wakes the normal idle-worker primitive for any parked sibling to pick it up.
                operation.CompleteFromIoUring(completion.Result);
            }
        }

        /// <summary>
        /// Implemented by types that can be submitted to <see cref="IoUringThreadPool"/> and receive
        /// their completion result back.
        /// </summary>
        internal interface IIoUringOperation
        {
            /// <summary>
            /// Called directly by the driver thread (synchronously, as part of draining the completion
            /// queue) with the raw io_uring completion result: the number of bytes transferred on
            /// success, or <c>-errno</c> on failure. Implementations must only do the minimal bookkeeping
            /// required (e.g., unpinning buffers, storing the result) and then queue the actual
            /// continuation via <see cref="ThreadPool.UnsafeQueueUserWorkItem(IThreadPoolWorkItem, bool)"/>
            /// - they must NOT run the continuation body inline on the driver thread.
            /// </summary>
            void CompleteFromIoUring(int result);
        }
    }
}
