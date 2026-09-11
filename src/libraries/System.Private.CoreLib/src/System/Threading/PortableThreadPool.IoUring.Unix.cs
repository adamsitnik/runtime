// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace System.Threading
{
    internal sealed partial class PortableThreadPool
    {
        /// <summary>
        /// Implements the "every Thread Pool thread is an io_uring thread, running continuations
        /// inline" architecture described in the io_uring design doc's "Optimal Thread Count" section:
        /// each Thread Pool worker thread lazily creates and owns its own private ring the first time
        /// it submits an io_uring operation. Only that thread ever submits to, or reaps completions
        /// from, its own ring - so no lock is needed anywhere (unlike the shared-ring/rotating-driver
        /// design this replaces). There is deliberately no work stealing: if the owning thread never
        /// loops back to drain its own ring (e.g. it's busy running other code, or - prevented
        /// separately, see <see cref="PortableThreadPool.WorkerThread.IsIOPending"/> - it would
        /// otherwise exit while operations are still in flight), that thread's completions simply do
        /// not get processed until it does. Completions are executed *inline*, directly on the owning
        /// thread as part of draining its ring - never redispatched through the Thread Pool queue. This
        /// is the highest-performance but riskiest option from the design doc: a continuation that
        /// blocks or runs long will stall that thread's ring (and therefore all I/O owned by it) for as
        /// long as it runs.
        /// </summary>
        internal static class IoUringThreadPool
        {
            // Depth of each per-thread ring's submission/completion queues. Not currently configurable.
            private const int QueueDepth = 1024;

            // Maximum number of completions fetched per IoRingWaitForCompletions call. Batching here
            // means a thread that wakes up to many simultaneously-ready completions on its own ring
            // drains all of them via a single syscall instead of one syscall per completion.
            private const int MaxCompletionsPerWait = 64;

            // Opt-out switch: io_uring integration is used by default on Linux when the kernel supports
            // it. Set DOTNET_USE_IO_URING=0 to fall back to the pre-existing (blocking-call-on-a-
            // ThreadPool-work-item) implementation unconditionally.
            private static readonly bool s_isEnabled = DetermineIsEnabled();

            /// <summary>Whether the io_uring Thread Pool integration is enabled and usable on this system.</summary>
            public static bool IsEnabled => s_isEnabled;

            private static bool DetermineIsEnabled()
            {
                if (!OperatingSystem.IsLinux())
                {
                    return false;
                }

                bool configuredOn =
                    AppContextConfigHelper.GetBooleanConfig("System.Threading.ThreadPool.UseIoUring", "DOTNET_USE_IO_URING", defaultValue: true);
                if (!configuredOn)
                {
                    return false;
                }

                return Interop.Sys.IoRingIsAvailable() != 0;
            }

            /// <summary>
            /// Per-thread ring state: the ring handle, how many operations submitted through it are
            /// still in flight, and a reusable scratch array for batching the (inline-executed) work
            /// items produced by a single drain pass. Exactly one instance ever exists per OS thread
            /// that has submitted at least one io_uring operation, stored in <see cref="t_ring"/> -
            /// never accessed by any other thread, so nothing here needs synchronization.
            /// </summary>
            private sealed class PerThreadRing
            {
                public readonly IntPtr Handle;
                public int InFlightCount;
                public readonly IThreadPoolWorkItem[] WorkItemBatch = new IThreadPoolWorkItem[MaxCompletionsPerWait];

                public PerThreadRing(IntPtr handle) => Handle = handle;
            }

            // Sentinel stored in t_ring for a thread whose own ring failed to create (e.g., a
            // process-wide fd/mmap resource limit was hit): never null (unlike a plain "not yet
            // created" state), so TrySubmit can distinguish "haven't tried yet" from "tried and
            // permanently failed" without retrying ring creation on every single call.
            private static readonly PerThreadRing s_failedRingSentinel = new PerThreadRing(IntPtr.Zero);

            [ThreadStatic]
            private static PerThreadRing? t_ring;

            /// <summary>
            /// Whether the calling thread currently has any io_uring operations in flight on its own
            /// ring. Used by <see cref="PortableThreadPool.WorkerThread.IsIOPending"/> to prevent a
            /// worker thread from exiting (and abandoning its ring, silently dropping in-flight
            /// completions forever - there is no work stealing in this architecture) while it still owns
            /// outstanding operations.
            /// </summary>
            public static bool CurrentThreadHasPendingOperations => t_ring is { InFlightCount: > 0 };

            /// <summary>
            /// Attempts to submit a single request on the calling thread's own ring (creating it lazily
            /// on first use). On success, the operation is now in flight and its completion will
            /// eventually be delivered - and executed *inline* - by this same thread, from
            /// <see cref="TryDriveOwnRing"/>, the next time it loops back before parking. Returns false
            /// if the request could not be submitted - e.g. the calling thread is not a Thread Pool
            /// worker thread (see remarks below), this thread's ring failed to create, or the submission
            /// queue is currently full - callers should fall back to their non-io_uring code path in
            /// that case, as no partial state is left behind.
            /// </summary>
            /// <remarks>
            /// Only genuine Thread Pool worker threads are allowed to use their own ring: they are the
            /// only threads that loop back through <see cref="PortableThreadPool.WorkerThread"/>'s
            /// dispatch loop and get a chance to call <see cref="TryDriveOwnRing"/> before parking. A
            /// non-worker thread (e.g. the main thread, a dedicated <see cref="Thread"/>, a timer
            /// callback thread) would have nothing that ever comes back to drain a ring it created,
            /// permanently leaking that operation's completion.
            /// </remarks>
            public static unsafe bool TrySubmit(IIoUringOperation operation, in Interop.Sys.IoRingRequest request)
            {
                if (!s_isEnabled || !Thread.CurrentThread.IsThreadPoolThread)
                {
                    return false;
                }

                PerThreadRing? ring = t_ring;
                if (ring is null)
                {
                    if (Interop.Sys.IoRingCreate(QueueDepth, QueueDepth, out IntPtr handle) != 0)
                    {
                        // Ring creation failed for this thread; remember that so we don't retry on every
                        // single submission attempt from this thread for the rest of its lifetime.
                        t_ring = s_failedRingSentinel;
                        return false;
                    }

                    ring = new PerThreadRing(handle);
                    t_ring = ring;
                }
                else if (ReferenceEquals(ring, s_failedRingSentinel))
                {
                    return false;
                }

                GCHandle handle2 = GCHandle.Alloc(operation);
                Interop.Sys.IoRingRequest localRequest = request;
                localRequest.UserData = (ulong)GCHandle.ToIntPtr(handle2);

                int result;
                int submittedCount;
                result = Interop.Sys.IoRingSubmit(ring.Handle, &localRequest, 1, out submittedCount);
                bool submitted = result == 0 && submittedCount == 1;

                if (submitted)
                {
                    // No other thread ever submits to this ring, so - unlike the shared-ring design -
                    // there is no "wave of concurrent submitters" to coalesce the kick across; every
                    // submission kicks its own ring immediately.
                    Interop.Sys.IoRingKick(ring.Handle);
                    ring.InFlightCount++;
                }
                else
                {
                    handle2.Free();
                }

                return submitted;
            }

            /// <summary>
            /// Called by this thread's own <see cref="PortableThreadPool.WorkerThread"/> dispatch loop,
            /// right before it would otherwise park (has no normal Thread Pool work left). If this
            /// thread has ever created its own ring and has at least one operation in flight on it,
            /// blocks in-kernel waiting for at least one completion, then executes the corresponding
            /// continuations *inline*, directly on this thread - never redispatched through the Thread
            /// Pool queue. Returns true if this thread drove (and should re-check for normal work before
            /// parking, since running a continuation inline may itself have queued new work), false if
            /// it has no ring, or nothing in flight, and should proceed to park normally.
            /// </summary>
            public static unsafe bool TryDriveOwnRing()
            {
                PerThreadRing? ring = t_ring;
                if (ring is null || ReferenceEquals(ring, s_failedRingSentinel) || ring.InFlightCount == 0)
                {
                    return false;
                }

                // Each call below fetches up to MaxCompletionsPerWait completions in a single syscall
                // (the native side already drains everything currently available up to that count), so
                // waking up to many simultaneously-ready completions does not need one syscall per
                // completion.
                Span<Interop.Sys.IoRingCompletion> completions = stackalloc Interop.Sys.IoRingCompletion[MaxCompletionsPerWait];

                int completedCount;
                fixed (Interop.Sys.IoRingCompletion* completionsPtr = completions)
                {
                    int result = Interop.Sys.IoRingWaitForCompletions(ring.Handle, completionsPtr, MaxCompletionsPerWait, minComplete: 1, out completedCount);
                    if (result != 0 || completedCount == 0)
                    {
                        return true;
                    }
                }

                RunInline(completions.Slice(0, completedCount), ring);

                // Drain any additional completions that are already available without waiting again.
                while (true)
                {
                    int nextCompletedCount;
                    fixed (Interop.Sys.IoRingCompletion* completionsPtr = completions)
                    {
                        int nextResult = Interop.Sys.IoRingWaitForCompletions(ring.Handle, completionsPtr, MaxCompletionsPerWait, minComplete: 0, out nextCompletedCount);
                        if (nextResult != 0 || nextCompletedCount == 0)
                        {
                            break;
                        }
                    }

                    RunInline(completions.Slice(0, nextCompletedCount), ring);
                }

                return true;
            }

            /// <summary>
            /// Completes the operation associated with each of the given completions and runs the
            /// resulting continuation (if any) *inline*, immediately, on the calling thread - this is
            /// the defining characteristic of this architecture. No Thread Pool re-dispatch happens
            /// here at all.
            /// </summary>
            private static void RunInline(ReadOnlySpan<Interop.Sys.IoRingCompletion> completions, PerThreadRing ring)
            {
                foreach (ref readonly Interop.Sys.IoRingCompletion completion in completions)
                {
                    ring.InFlightCount--;

                    GCHandle handle = GCHandle.FromIntPtr((IntPtr)completion.UserData);
                    var operation = (IIoUringOperation)handle.Target!;
                    handle.Free();

                    // CompleteFromIoUring only does minimal bookkeeping and returns the work item (if
                    // any) representing the continuation to run - executed inline here rather than
                    // queued, unlike the shared-ring/batched-queuing design this file replaces.
                    IThreadPoolWorkItem? workItem = operation.CompleteFromIoUring(completion.Result);
                    workItem?.Execute();
                }
            }
        }

        /// <summary>
        /// Implemented by types that can be submitted to <see cref="IoUringThreadPool"/> and receive
        /// their completion result back.
        /// </summary>
        internal interface IIoUringOperation
        {
            /// <summary>
            /// Called directly by the owning thread (synchronously, as part of draining its own ring)
            /// with the raw io_uring completion result: the number of bytes transferred on success, or
            /// <c>-errno</c> on failure. Implementations must only do the minimal bookkeeping required
            /// (e.g., unpinning buffers, storing the result) and must NOT run the continuation body
            /// inline themselves, nor queue it to the Thread Pool. Instead, return the
            /// <see cref="IThreadPoolWorkItem"/> representing the continuation to run, so the caller can
            /// execute it (inline, in this architecture). Return <see langword="null"/> if this
            /// completion does not (yet) require a continuation to run - e.g. a partial write was
            /// resubmitted via a new io_uring request and remains in flight.
            /// </summary>
            IThreadPoolWorkItem? CompleteFromIoUring(int result);
        }
    }
}
