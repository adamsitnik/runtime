// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Threading
{
    internal sealed partial class PortableThreadPool
    {
        /// <summary>
        /// Implements a single-issuer io_uring/ThreadPool integration: each ring is created with
        /// <c>IORING_SETUP_SINGLE_ISSUER</c> together with <c>IORING_SETUP_DEFER_TASKRUN</c>, so the
        /// kernel can skip its internal ring-wide lock - at the cost of requiring every
        /// <c>io_uring_enter</c> call (submission *and* completion-wait calls alike) to come from the
        /// same fixed OS thread for the ring's whole lifetime. This was originally attempted with
        /// completion-reaping still rotating across arbitrary Thread Pool worker threads (as in the
        /// shared-ring design) and without <c>DEFER_TASKRUN</c>, but that does not work: the kernel's
        /// single-issuer check applies to *any* <c>io_uring_enter</c> call, including a plain
        /// <c>IORING_ENTER_GETEVENTS</c> wait with nothing to submit - so whichever thread happened to
        /// call in first (a submission, or an unrelated worker thread reaping completions) would
        /// permanently "claim" the ring, and every other thread's calls would fail with <c>-EEXIST</c>
        /// forever. This was confirmed empirically (a worker thread won the race to reap completions
        /// before the intended submitter thread ever got to submit, hanging the process) and matches the
        /// kernel's actual <c>submitter_task</c> check, which is not scoped to "submission calls only".
        ///
        /// Consequently, a single dedicated background thread per <see cref="Ring"/> (not a Thread Pool
        /// worker, and not counted in Thread Pool accounting/hill-climbing) owns *both* submission and
        /// completion-reaping for that ring's entire lifetime - this is the only way to actually use
        /// <c>IORING_SETUP_SINGLE_ISSUER</c> correctly, and since that constraint already forces both
        /// roles onto one thread, requesting <c>DEFER_TASKRUN</c> as well is free extra performance with
        /// no further downside: it just means this same thread must periodically call
        /// <c>io_uring_enter(..., IORING_ENTER_GETEVENTS)</c> - which it already needs to do to reap
        /// completions - to pump the kernel's deferred task-work (see
        /// <c>SystemNative_IoRingWaitForCompletions</c>'s native-side doc comment for why this call cannot
        /// be skipped even when nothing is known to be ready). Any other thread that wants to submit a
        /// request enqueues it into a lock-free MPSC queue and wakes the issuer thread by writing to a
        /// shared eventfd registered on the ring (<c>IORING_REGISTER_EVENTFD</c>, see
        /// <see cref="Ring.WakeEventFd"/>'s doc comment); the issuer thread drains the queue in batches,
        /// submits them, then polls for completions and waits on that same eventfd for either a new
        /// submission or a completion becoming ready (see <see cref="IssuerLoop"/>), rather than spinning
        /// or busy-polling. Each ring's submission queue is unbounded, so <see cref="TrySubmit"/> always
        /// succeeds (once enabled) - there is no "ring is full, fall back" signal in this design. Worker
        /// threads no longer participate in reaping completions at all; each ring's dedicated issuer
        /// thread owns that exclusively, since IORING_SETUP_SINGLE_ISSUER requires it.
        ///
        /// Rather than a single global ring, this is sharded across <see cref="s_rings"/> - a
        /// configurable number of independent <see cref="Ring"/> instances, each with its own ring
        /// handle, wake-eventfd, queues, and dedicated issuer thread (see <see cref="GetRingCount"/>).
        /// This exists to avoid a single issuer thread becoming a hard, non-scaling bottleneck: profiling
        /// (see this file's git history / design notes) showed a single ring's issuer thread executes
        /// every socket's actual recv/poll syscall work inline as part of <c>io_uring_enter</c>, so its
        /// absolute throughput is capped regardless of how many cores are otherwise available. Every
        /// request is routed to a ring by its own <c>fd</c> (see <see cref="GetRing"/>) - not by which
        /// thread happens to be calling <see cref="TrySubmit"/> - so a given fd's requests always land
        /// on the same ring/issuer thread regardless of which thread submits them, instead of being
        /// scattered arbitrarily across all of them. This also makes single-fd cancellation simple: the
        /// same <c>fd -&gt; ring</c> mapping used to submit an operation is used to find the one ring
        /// that could possibly have it in flight, without needing to track which ring a given fd's
        /// operation actually landed on.
        ///
        /// A second, related gotcha (also confirmed empirically via a standalone native repro, not
        /// documented in the man page): a IORING_SETUP_SINGLE_ISSUER ring's fixed "owning" thread is
        /// whichever thread calls <c>io_uring_setup(2)</c> - not whichever thread happens to make the
        /// first <c>io_uring_enter(2)</c> call afterwards. So a ring cannot be created eagerly (e.g. in
        /// the static constructor) and then only *entered* from its dedicated issuer thread; each ring
        /// must be created by its own issuer thread, as the very first thing it does.
        ///
        /// That, in turn, creates a third gotcha, this one a plain CLR type-initialization deadlock
        /// rather than anything io_uring-specific: the static constructor cannot simply start an issuer
        /// thread with <see cref="IssuerLoop"/> as its entry point and then block waiting for it to
        /// report back the ring handle, because entering <see cref="IssuerLoop"/> - a member of this very
        /// type - requires this type to have finished initializing first, and the CLR blocks any thread
        /// other than the one currently running a type's static constructor from doing that. The static
        /// constructor below therefore performs each ring-creation handshake using only captured locals
        /// and the (non-static) <see cref="Ring"/> instance itself - never touching this type's own
        /// static fields from the new thread - and only once every ring's handshake completes does *this*
        /// (the constructor's own) thread publish <see cref="s_isEnabled"/>/<see cref="s_rings"/> itself,
        /// before returning. Only after that does each new thread go on to call <see cref="IssuerLoop"/>.
        /// </summary>
        internal static partial class IoUringThreadPool
        {
            // Depth of the shared submission/completion queues. Not currently configurable; may become
            // adaptive in a future iteration.
            private const int QueueDepth = 1024;
            private const ulong OperationSlotTag = 1;
            private const int OperationSlotGenerationShift = 32;

            // Maximum number of completions fetched per IoRingWaitForCompletions call. Batching here
            // means the issuer thread, when it wakes up to many simultaneously-ready completions (e.g.
            // under high concurrency), drains all of them via a single syscall instead of one syscall per
            // completion.
            private const int MaxCompletionsPerWait = 64;
            private const int MaxCompletionsPerTurn = 256;
            private const int SubmissionRetryDelayMs = 1;

            // Defensive safety-net timeout (milliseconds) for the issuer thread's wait when operations
            // are in flight but nothing is immediately ready. In the common/expected case this timeout
            // never actually elapses: the registered eventfd (see Ring.WakeEventFd) is expected to wake
            // the issuer thread directly whenever deferred completion task-work becomes ready to run -
            // this is the documented intent of pairing IORING_SETUP_DEFER_TASKRUN with a registered
            // eventfd (see SystemNative_IoRingRegisterEventFd's doc comment). This bound exists only to
            // self-heal (within at most this many milliseconds) if that assumption ever turns out to be
            // wrong for some request type/kernel version - trading a small amount of worst-case
            // completion-latency for defense in depth, without reintroducing the tight busy-poll loop
            // this design replaced.
            private const int InFlightWaitTimeoutMs = 1000;

            private const int CompletionProcessorTimeSliceMs = 15;

            // Maximum number of requests the issuer thread pulls off a ring's pending-submissions queue
            // and passes to a single Interop.Sys.IoRingSubmit call. Bounds the size of the reused scratch
            // array and gives the ring a chance to be kicked (and start processing) partway through a
            // very large burst, rather than waiting for the entire burst to be dequeued first.
            private const int MaxRequestsPerSubmitBatch = 256;

            // Set DOTNET_IORING_PARALLELIZED_ENQUEUE=0 to compare with the legacy batched dispatch.
            private static readonly bool s_useParallelizedEnqueue =
                AppContextConfigHelper.GetBooleanConfig("System.Threading.ThreadPool.IoUringParallelizedEnqueue", "DOTNET_IORING_PARALLELIZED_ENQUEUE", defaultValue: true);

            // Opt-out switch: io_uring integration is used by default on Linux when the kernel supports
            // it. Set DOTNET_USE_IO_URING=0 to fall back to the pre-existing (blocking-call-on-a-
            // ThreadPool-work-item) implementation unconditionally.
            //
            // Not readonly, unlike the usual pattern in the other io_uring architectures: its final value
            // depends on whether every dedicated issuer thread (started from the static constructor)
            // manages to create its ring - see the static constructor's doc comment for the full
            // explanation, including why it is the constructor's own thread, not any issuer thread, that
            // actually assigns this field.
            private static bool s_isEnabled;

            // One independent single-issuer ring per shard, sized by GetRingCount(). Assigned at most
            // once, by the static constructor's own thread, right before it returns - see s_isEnabled's
            // doc comment. Null (and unused) if s_isEnabled is false.
            private static Ring[]? s_rings;

            internal struct OperationSlot
            {
                public IIoUringOperation? Operation;
                public uint Generation;

                // Reference count protecting against out-of-order completion processing: a multishot
                // operation (e.g. Interop.Sys.IoRingOp.RecvMultishot) can have several of its completions
                // dequeued (in order) but then *processed* concurrently, in any order, by independent
                // Thread Pool workers - so the slot cannot simply be freed the instant a completion
                // without the "More" flag is seen, since an earlier (non-final) completion for the same
                // operation might still be mid-flight on another worker at that moment. See
                // RetainOperationToken/ReleaseOperationToken.
                public int RefCount;

                // Next 0-based delivery sequence number to hand out for this operation's completions -
                // assigned once per completion, by the single issuer thread, strictly in the order
                // completions were dequeued off the ring (see RetainOperationToken). Since the completions
                // themselves may go on to be *processed* out of order by independent workers, this value
                // lets a multishot operation (see MultishotReceiveOperation) recover the correct delivery
                // order without a lock, by having each worker wait for its own sequence number to become
                // "next" before actually invoking the caller's callback.
                public long NextSequence;
            }

            /// <summary>
            /// GCHandle target used when a ring's bounded <see cref="OperationSlot"/> array is exhausted
            /// (see <see cref="TrySubmit"/>) - carries the same reference-counting fields as
            /// <see cref="OperationSlot"/>, for the same reason (see its doc comment), since a raw
            /// <see cref="GCHandle"/> has no room for auxiliary per-token state of its own.
            /// </summary>
            private sealed class GCHandleToken
            {
                public readonly IIoUringOperation Operation;
                public int RefCount = 1;
                public long NextSequence;

                public GCHandleToken(IIoUringOperation operation) => Operation = operation;
            }

            /// <summary>
            /// Per-shard state: one independent <c>IORING_SETUP_SINGLE_ISSUER</c> ring, its own
            /// submission/completion queues, wake-eventfd, and dedicated issuer thread. See this type's
            /// own doc comment (on <see cref="IoUringThreadPool"/>) for why sharding across multiple
            /// rings/issuer threads exists and how callers are assigned to one.
            /// </summary>
            internal sealed class Ring
            {
                // The index this ring was created at (0-based) - used only for the issuer thread's name,
                // to make multiple issuer threads distinguishable in a debugger/process list.
                public readonly int Index;

                // This ring's handle, or IntPtr.Zero if unavailable/disabled. Assigned at most once, by
                // this ring's own dedicated issuer thread, before that thread signals readiness back to
                // the static constructor - see the static constructor's doc comment.
                public IntPtr RingHandle;

                // Issuer-owned count of requests taken from PendingSubmissions whose CQEs have not
                // been reaped. Managed callbacks need not finish before the issuer can park.
                public int InFlightCount;

                // MPSC hand-off from any thread calling TrySubmit (for a request whose fd routes to this
                // ring - see GetRing) to this ring's single dedicated issuer thread (see IssuerLoop). This
                // ring was created with IORING_SETUP_SINGLE_ISSUER, so only that one thread is permitted
                // to ever call Interop.Sys.IoRingSubmit/IoRingKick/IoRingWaitForCompletions for it - every
                // other thread must go through this queue instead. Unbounded: TrySubmit never blocks or
                // fails due to this queue being "full".
                public readonly ConcurrentQueue<Interop.Sys.IoRingRequest> PendingSubmissions = new();

                // An eventfd registered with this ring via IORING_REGISTER_EVENTFD (see
                // Interop.Sys.IoRingRegisterEventFd), or -1 if unavailable. The kernel bumps its counter
                // (making it readable) whenever a CQE is posted - including, per the documented intent of
                // pairing IORING_SETUP_DEFER_TASKRUN with a registered eventfd, when *deferred* completion
                // task-work becomes ready to run, even though it has not been posted to the CQ yet.
                // TrySubmit also writes to this same fd directly (see Interop.Sys.EventFdWrite) to wake
                // this ring's issuer thread when it enqueues a new request. Interop.Sys.EventFdWait is a
                // real (poll(2)-based) kernel wait with no userland spin, and unifies both wake reasons
                // (new submission, and completion becoming ready) onto the one fd/one wait call instead of
                // needing a separate bounded poll interval for each. Assigned at most once, by this ring's
                // own dedicated issuer thread, before that thread signals readiness.
                public int WakeEventFd = -1;

                // Coalescing flag for TrySubmit's wake-up signal on this ring: 0 means no thread has
                // signaled this ring's issuer since its last reset, 1 means one already has (so no
                // further EventFdWrite syscall is needed until the issuer resets it again). Turns any
                // number of concurrent TrySubmit calls (assigned to this ring) between two issuer wake
                // cycles into at most one EventFdWrite syscall, without risking a missed wake-up - see
                // TrySubmit and IssuerLoop for the reset-then-recheck protocol that makes this safe.
                public int WakeSignaled;

                // One issuer produces completions; multiple workers may consume them. Each entry also
                // carries the delivery sequence number RetainOperationToken assigned it, since
                // ConcurrentQueue's own FIFO dequeue order does not guarantee the *processing* of two
                // dequeued completions happens in that same order once handed to independent workers -
                // see MultishotReceiveOperation, the only current consumer that cares.
                public readonly ConcurrentQueue<(Interop.Sys.IoRingCompletion Completion, long Sequence)> CompletionQueue = new();
                // Running processors may overlap, but at most one additional processor is queued.
                public int CompletionProcessingRequested;
                public readonly IThreadPoolWorkItem CompletionProcessor;
                public readonly OperationSlot[] OperationSlots = new OperationSlot[QueueDepth];
                public readonly ConcurrentQueue<int> FreeOperationSlots = new();

                // This ring's provided-buffer group zero (see SystemNative_IoRingRegisterBufferRing),
                // used by IoRingOp_RecvMultishot; null until registered by the static constructor's
                // handshake, alongside RingHandle/WakeEventFd.
                public ReceiveBufferPool? ReceiveBuffers;

                // Consumed buffer ids a ReceiveBufferLease.Dispose() (on any thread) wants returned to
                // the kernel. MPSC, mirroring PendingSubmissions: any thread may enqueue, only the issuer
                // dequeues (see IssuerLoop) - draining opportunistically each loop iteration rather than
                // waking the issuer just to return a single buffer (a receive-heavy ring is already
                // waking up on its own on essentially every loop iteration to reap new completions).
                public readonly ConcurrentQueue<ushort> PendingBufferReturns = new();

                // fd -> the UserData token of that fd's currently in-flight RecvMultishot request, so
                // IoUringThreadPool.TryCancelReceiveMultishot can find the right token to cancel without
                // a separate registry: this is looked up via the exact same fd -> ring mapping (GetRing)
                // used to submit the request in the first place. Entries are added right after a
                // successful submission and removed on that operation's final completion (see
                // MultishotReceiveOperation).
                public readonly ConcurrentDictionary<IntPtr, ulong> ActiveMultishotReceives = new();

                public Ring(int index)
                {
                    Index = index;
                    CompletionProcessor = new CompletionProcessorWorkItem(this);
                    for (int i = 0; i < OperationSlots.Length; i++)
                    {
                        FreeOperationSlots.Enqueue(i);
                    }
                }
            }

#pragma warning disable CA1810 // remove the explicit static constructor
            static IoUringThreadPool()
            {
                bool isEligible = IsEligible();
                if (!isEligible)
                {
                    s_isEnabled = false;
                    return;
                }

                int ringCount = GetRingCount();
                int receiveBufferSize = GetReceiveBufferSize();
                int receiveBufferCount = GetReceiveBufferCount();
                var rings = new Ring[ringCount];
                bool allCreated = true;

                for (int i = 0; i < ringCount; i++)
                {
                    // The ring itself cannot be created here (on this, the static constructor's own
                    // thread): IORING_SETUP_SINGLE_ISSUER binds a ring's single fixed owning thread to
                    // whichever thread calls io_uring_setup(2) - *not* to whichever thread happens to make
                    // the first io_uring_enter(2) call, as originally (incorrectly) assumed. This was
                    // confirmed empirically with a standalone native repro: a second thread's very first
                    // io_uring_enter call on a ring created by another thread fails with -EEXIST
                    // immediately, even though it is that second thread's first-ever call on the ring. So
                    // each ring must be created by the same dedicated thread that will go on to be the one
                    // and only thread ever calling IoRingSubmit/IoRingKick/IoRingWaitForCompletions for it
                    // - i.e., by a new, dedicated issuer thread, as the very first thing it does.
                    //
                    // The handshake below is deliberately written to avoid touching any static member of
                    // IoUringThreadPool from the new thread: the CLR only allows the thread that is
                    // currently running a type's static constructor to freely access that type's own
                    // static members while doing so; any *other* thread's attempt to access them
                    // (including merely calling one of the type's other static methods, such as
                    // IssuerLoop) blocks until the constructor completes. The new thread is instead only
                    // ever given a reference to its own (non-static) Ring instance - assigning that
                    // object's own instance fields (RingHandle, WakeEventFd) from the new thread is safe,
                    // since those are not static members of IoUringThreadPool. Only this thread - which is
                    // allowed to, since it is the one actually running the static constructor - assigns
                    // s_isEnabled/s_rings themselves, once every ring's handshake completes.
                    using ManualResetEventSlim readyToRun = new(initialState: false);
                    var ring = new Ring(i);
                    bool created = false;

                    var issuerThread = new Thread(() =>
                    {
                        // singleIssuer: true - the whole point of this architecture is that only this
                        // thread (which just called io_uring_setup(2) here, and will be the only thread
                        // that ever touches this ring from now on) ever touches it, so the kernel can skip
                        // its internal ring-wide lock.
                        int result = Interop.Sys.IoRingCreate(QueueDepth, QueueDepth, singleIssuer: 1, out IntPtr ringHandle);
                        created = result == 0;
                        ring.RingHandle = ringHandle;

                        if (created)
                        {
                            ring.WakeEventFd = Interop.Sys.IoRingRegisterEventFd(ring.RingHandle);
                            created = ring.WakeEventFd >= 0;
                        }

                        if (created)
                        {
                            // Registers this ring's provided-buffer group zero for RecvMultishot. Per
                            // the mandatory HAVE_LINUX_IO_URING_H check (see configure.cmake), a kernel
                            // that supports io_uring at all also supports this - so a failure here is
                            // treated exactly like a failure to create the ring or register its eventfd:
                            // this ring (and, transitively, the whole io_uring integration - see
                            // s_isEnabled below) is abandoned in favor of the non-io_uring fallback path.
                            unsafe
                            {
                                byte* bufferStorage = null;
                                int registerResult = Interop.Sys.IoRingRegisterBufferRing(
                                    ring.RingHandle, receiveBufferSize, receiveBufferCount, &bufferStorage);
                                created = registerResult == 0;
                                if (created)
                                {
                                    ring.ReceiveBuffers = new ReceiveBufferPool(ring, receiveBufferSize, receiveBufferCount, bufferStorage);
                                }
                            }
                        }

                        readyToRun.Set();

                        if (created)
                        {
                            // IssuerLoop is a member of IoUringThreadPool, so entering it may briefly
                            // block this thread here until the static constructor - which is waiting on
                            // readyToRun.Wait() right after starting this thread - observes the Set()
                            // above and moves on. That is expected, bounded, and not a deadlock: by this
                            // point the constructor no longer depends on this thread for anything, so it
                            // (and every other ring's handshake still pending) will finish and return
                            // almost immediately, unblocking this call.
                            IssuerLoop(ring);
                        }
                    })
                    {
                        IsBackground = true,
                        Name = $".NET IoUring Issuer #{i}",
                    };
                    issuerThread.Start();

                    // Block until this ring's issuer thread has created it (or failed to) before moving
                    // on to the next ring. This keeps the external contract identical to every other
                    // io_uring architecture in this codebase: once the static constructor returns,
                    // IsEnabled/TrySubmit are immediately usable with their final, fully-initialized
                    // values, regardless of which thread(s) actually performed ring creation.
                    readyToRun.Wait();

                    if (!created)
                    {
                        allCreated = false;
                        break;
                    }

                    rings[i] = ring;
                }

                s_isEnabled = allCreated;
                if (allCreated)
                {
                    s_rings = rings;
                }
            }
#pragma warning restore CA1810

            /// <summary>Whether the io_uring Thread Pool integration is enabled and usable on this system.</summary>
            public static bool IsEnabled => s_isEnabled;

            private static bool IsEligible()
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
            /// Number of independent single-issuer rings (and dedicated issuer threads) to create. Set
            /// DOTNET_IORING_THREAD_COUNT (or the equivalent AppContext switch) to an explicit positive
            /// value to override; otherwise defaults to one ring per 8 cores (rounded down), with a
            /// minimum of 1. This is only ever read once, from the static constructor - changing it after
            /// startup has no effect.
            /// </summary>
            private static int GetRingCount()
            {
                const int DefaultCoresPerRing = 8;

                int configured = AppContextConfigHelper.GetInt32Config(
                    "System.Threading.ThreadPool.IoUringThreadCount", "DOTNET_IORING_THREAD_COUNT", defaultValue: 0, allowNegative: false);
                if (configured > 0)
                {
                    return configured;
                }

                return Math.Max(1, Environment.ProcessorCount / DefaultCoresPerRing);
            }

            /// <summary>
            /// Size, in bytes, of each provided buffer in a ring's RecvMultishot buffer pool. Set
            /// DOTNET_IORING_RECV_BUFFER_SIZE to override; defaults to 16 KiB. Read once per ring, from
            /// the static constructor.
            /// </summary>
            private static int GetReceiveBufferSize() =>
                AppContextConfigHelper.GetInt32Config(
                    "System.Threading.ThreadPool.IoUringReceiveBufferSize", "DOTNET_IORING_RECV_BUFFER_SIZE",
                    defaultValue: 16 * 1024, allowNegative: false);

            /// <summary>
            /// Number of provided buffers in a ring's RecvMultishot buffer pool (must be a power of two -
            /// see SystemNative_IoRingRegisterBufferRing). Set DOTNET_IORING_RECV_BUFFER_COUNT to
            /// override; defaults to 128. Read once per ring, from the static constructor.
            /// </summary>
            private static int GetReceiveBufferCount() =>
                AppContextConfigHelper.GetInt32Config(
                    "System.Threading.ThreadPool.IoUringReceiveBufferCount", "DOTNET_IORING_RECV_BUFFER_COUNT",
                    defaultValue: 128, allowNegative: false);

            /// <summary>
            /// Returns the <see cref="Ring"/> that <paramref name="fd"/> is routed to: every request for
            /// a given fd is routed to the same ring regardless of which thread submits it (unlike a
            /// per-calling-thread assignment), both so that a fd's requests stay concentrated on one
            /// ring/issuer thread for better cache/data affinity, and so that a single fd's in-flight
            /// operation(s) can always be found (e.g. to cancel) via this same, trivially-recomputable
            /// mapping - no separate fd -&gt; ring registry is needed. fd allocation on Unix is a small,
            /// densely-packed monotonically-increasing counter (reused as fds close), so a plain modulo
            /// spreads load reasonably evenly across rings without needing a fancier hash.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static Ring GetRing(IntPtr fd)
            {
                Ring[] rings = s_rings!;
                int index = (int)((uint)(nuint)(nint)fd % (uint)rings.Length);
                return rings[index];
            }

            /// <summary>
            /// Attempts to submit a single request to the ring <paramref name="request"/>'s fd is routed
            /// to (see <see cref="GetRing"/>). Unlike the other io_uring architectures in this codebase,
            /// this never actually fails once <see cref="IsEnabled"/> is true: the request is simply
            /// enqueued for that ring's dedicated issuer thread to submit, and this method returns
            /// immediately. The operation is now considered in flight; its completion will eventually be
            /// delivered via <see cref="IIoUringOperation.CompleteFromIoUring(int, uint, long)"/>, invoked on a
            /// Thread Pool work item. The queue backing this hand-off is unbounded - under sustained
            /// overload (submissions arriving faster than the kernel/NIC can drain them), memory usage
            /// here could grow without bound; this is a known, accepted limitation of this experimental
            /// architecture, not an oversight.
            /// </summary>
            public static bool TrySubmit(IIoUringOperation operation, in Interop.Sys.IoRingRequest request) =>
                TrySubmit(operation, in request, out _);

            /// <summary>
            /// Same as <see cref="TrySubmit(IIoUringOperation, in Interop.Sys.IoRingRequest)"/>, but also
            /// reports the <c>UserData</c> token this request was actually assigned - needed by callers
            /// (e.g. <see cref="TrySubmitReceiveMultishot"/>) that must later be able to target this
            /// exact request for cancellation (<see cref="Interop.Sys.IoRingOp.Cancel"/>'s Offset).
            /// </summary>
            public static bool TrySubmit(IIoUringOperation operation, in Interop.Sys.IoRingRequest request, out ulong userData)
            {
                Debug.Assert(s_isEnabled);

                Ring ring = GetRing(request.Fd);

                Interop.Sys.IoRingRequest localRequest = request;
                if (ring.FreeOperationSlots.TryDequeue(out int slotIndex))
                {
                    ref OperationSlot slot = ref ring.OperationSlots[slotIndex];
                    uint generation = unchecked(slot.Generation + 1);
                    Volatile.Write(ref slot.Generation, generation);
                    // 1 is a bias representing "no completion has retired this slot yet" - see
                    // RetainOperationToken/ReleaseOperationToken.
                    Volatile.Write(ref slot.RefCount, 1);
                    Volatile.Write(ref slot.NextSequence, 0);
                    Volatile.Write(ref slot.Operation, operation);
                    localRequest.UserData = ((ulong)generation << OperationSlotGenerationShift) |
                        ((uint)slotIndex << 1) | OperationSlotTag;
                }
                else
                {
                    // Normal GCHandles have bit zero clear; tagged slot tokens instead refer to
                    // the ring's bounded array, which roots their operations until completion.
                    GCHandle handle = GCHandle.Alloc(new GCHandleToken(operation));
                    localRequest.UserData = (ulong)GCHandle.ToIntPtr(handle);
                    Debug.Assert((localRequest.UserData & OperationSlotTag) == 0);
                }

                userData = localRequest.UserData;

                try
                {
                    ring.PendingSubmissions.Enqueue(localRequest);
                }
                catch
                {
                    // Nothing was ever submitted for this token, so no completion will ever arrive -
                    // free it unconditionally rather than going through the completion-counted release.
                    AbortOperationToken(ring, localRequest.UserData);
                    throw;
                }

                // Only the thread that wins the 0->1 transition actually writes to the eventfd; every
                // other concurrent caller (assigned to this same ring) can rely on that single write to
                // wake the issuer, since the issuer only resets this flag back to 0 immediately before it
                // is about to re-check the queue/wait (see IssuerLoop) - so any enqueue that raced with a
                // reset either gets "counted" by winning this Exchange itself, or is safely picked up by
                // the issuer's own post-reset recheck of the queue.
                if (Interlocked.Exchange(ref ring.WakeSignaled, 1) == 0)
                {
                    Interop.Sys.EventFdWrite(ring.WakeEventFd);
                }

                return true;
            }

            /// <summary>
            /// Body of a single dedicated issuer thread, once <paramref name="ring"/> has already been
            /// created (by this same thread - see the static constructor) and its
            /// <see cref="Ring.RingHandle"/>/<see cref="Ring.WakeEventFd"/> have been published by it.
            /// Every iteration submits whatever is currently queued in
            /// <see cref="Ring.PendingSubmissions"/>, then drains and dispatches whatever completions are
            /// already available. If nothing at all is in flight and the queue is empty, parks
            /// indefinitely on <see cref="Ring.WakeEventFd"/> until <see cref="TrySubmit"/> writes to it.
            /// If something is in flight but nothing was immediately ready, waits on that same fd with a
            /// defensive bounded timeout (<see cref="InFlightWaitTimeoutMs"/>) instead of an indefinite
            /// one, purely as a safety net - see <see cref="InFlightWaitTimeoutMs"/>'s doc comment for why
            /// the expected/common case does not actually rely on this bound elapsing.
            /// </summary>
            private static void IssuerLoop(Ring ring)
            {
                // Reused across every iteration. Only ever accessed by this single dedicated thread, so
                // no synchronization is needed for these arrays.
                var submitBatch = new Interop.Sys.IoRingRequest[MaxRequestsPerSubmitBatch];
                var completionsBatch = new Interop.Sys.IoRingCompletion[MaxCompletionsPerWait];
                var sequenceBatch = new long[MaxCompletionsPerWait];
                var workItemBatch = new IThreadPoolWorkItem[MaxCompletionsPerWait];
                var bufferReturnBatch = new ushort[MaxCompletionsPerWait];

                while (true)
                {
                    DrainAndSubmit(ring, submitBatch, completionsBatch, sequenceBatch, workItemBatch);
                    bool moreCompletions = DrainCompletions(ring, completionsBatch, sequenceBatch, workItemBatch);

                    // Opportunistic only: a ReceiveBufferLease.Dispose() on any other thread never wakes
                    // this issuer just to return one buffer (see Ring.PendingBufferReturns) - buffers sit
                    // there until this thread is next awake anyway (e.g. for a completion or submission),
                    // at which point republishing them costs no syscall (see IoRingReturnBuffers).
                    DrainReceiveBufferReturns(ring, bufferReturnBatch);

                    if (moreCompletions || !ring.PendingSubmissions.IsEmpty)
                    {
                        // Something was enqueued while we were draining; go around again immediately
                        // instead of waiting.
                        continue;
                    }

                    // Reset the wake-coalescing flag (see TrySubmit and Ring.WakeSignaled) before
                    // waiting, so that any TrySubmit call for this ring from here on is guaranteed to win
                    // the 0->1 transition and signal us. Then re-check the queue: a TrySubmit call could
                    // have raced with this very reset (observed the flag as still 1 from a *previous*
                    // cycle, so skipped its own EventFdWrite, right before we set it back to 0) - the
                    // recheck below is what catches that case and avoids a missed wake-up, instead of
                    // relying on the write that thread decided not to do.
                    Volatile.Write(ref ring.WakeSignaled, 0);
                    if (!ring.PendingSubmissions.IsEmpty)
                    {
                        continue;
                    }

                    int timeoutMs = ring.InFlightCount > 0 ? InFlightWaitTimeoutMs : -1;
                    if (Interop.Sys.EventFdWait(ring.WakeEventFd, timeoutMs) < 0)
                    {
                        Environment.FailFast($"io_uring eventfd wait failed: {Marshal.GetLastPInvokeError()}.");
                    }
                }
            }

            /// <summary>
            /// Republishes every buffer id currently queued in <see cref="Ring.PendingBufferReturns"/> to
            /// the kernel, in batches of at most <paramref name="batch"/>'s length. No syscall is needed
            /// (see <see cref="Interop.Sys.IoRingReturnBuffers"/>); this only runs on the issuer thread.
            /// </summary>
            private static unsafe void DrainReceiveBufferReturns(Ring ring, ushort[] batch)
            {
                if (ring.PendingBufferReturns.IsEmpty)
                {
                    return;
                }

                int count;
                while ((count = DequeueBufferReturnBatch(ring, batch)) > 0)
                {
                    fixed (ushort* batchPtr = batch)
                    {
                        if (Interop.Sys.IoRingReturnBuffers(ring.RingHandle, batchPtr, count) != 0)
                        {
                            Environment.FailFast($"io_uring provided-buffer return failed: {Marshal.GetLastPInvokeError()}.");
                        }
                    }
                }
            }

            private static int DequeueBufferReturnBatch(Ring ring, ushort[] batch)
            {
                int count = 0;
                while (count < batch.Length && ring.PendingBufferReturns.TryDequeue(out ushort bufferId))
                {
                    batch[count++] = bufferId;
                }

                return count;
            }

            /// <summary>
            /// Publishes at most one submission batch, then yields to completion reaping. The following
            /// completion wait submits these SQEs and processes deferred work in the same enter.
            /// </summary>
            private static unsafe void DrainAndSubmit(Ring ring, Interop.Sys.IoRingRequest[] batch,
                Interop.Sys.IoRingCompletion[] completionsBatch, long[] sequenceBatch, IThreadPoolWorkItem[] workItemBatch)
            {
                int count = 0;
                while (count < batch.Length && ring.PendingSubmissions.TryDequeue(out Interop.Sys.IoRingRequest request))
                {
                    batch[count++] = request;
                }

                if (count == 0)
                {
                    return;
                }

                ring.InFlightCount += count;
                fixed (Interop.Sys.IoRingRequest* batchPtr = batch)
                {
                    SubmitBatchWithRetry(ring, batchPtr, count, completionsBatch, sequenceBatch, workItemBatch);
                }
            }

            /// <summary>
            /// Submits every request in <paramref name="requestsPtr"/>[0..<paramref name="count"/>) to
            /// <paramref name="ring"/>, retrying only the not-yet-submitted remainder if the ring's
            /// submission queue is momentarily full (<c>submittedCount</c> less than requested), instead
            /// of re-enqueueing the remainder back into <see cref="Ring.PendingSubmissions"/> - doing the
            /// latter could reorder this batch behind requests enqueued by other threads afterwards, and
            /// would also unnecessarily perturb FIFO-ish submission order for no benefit, since this
            /// thread is the only one that will ever process this ring's queue anyway.
            /// </summary>
            private static unsafe void SubmitBatchWithRetry(Ring ring, Interop.Sys.IoRingRequest* requestsPtr, int count,
                Interop.Sys.IoRingCompletion[] completionsBatch, long[] sequenceBatch, IThreadPoolWorkItem[] workItemBatch)
            {
                int remaining = count;
                Interop.Sys.IoRingRequest* remainingPtr = requestsPtr;

                while (remaining > 0)
                {
                    int result = Interop.Sys.IoRingSubmit(ring.RingHandle, remainingPtr, remaining, out int submittedCount);
                    if (result != 0)
                    {
                        Environment.FailFast($"io_uring SQ publication failed: {Marshal.GetLastPInvokeError()}.");
                    }

                    if (submittedCount >= remaining)
                    {
                        return;
                    }

                    remainingPtr += submittedCount;
                    remaining -= submittedCount;

                    // This issuer must enter the kernel to consume SQEs; spinning alone cannot make room.
                    DrainCompletions(ring, completionsBatch, sequenceBatch, workItemBatch);
                }
            }

            /// <summary>
            /// Drains a bounded number of completions without waiting. Returns true when the budget
            /// was exhausted, so the issuer alternates with submissions rather than parking.
            /// </summary>
            private static unsafe bool DrainCompletions(Ring ring, Interop.Sys.IoRingCompletion[] completionsBatch, long[] sequenceBatch, IThreadPoolWorkItem[] workItemBatch)
            {
                int processed = 0;
                while (processed < MaxCompletionsPerTurn)
                {
                    int completedCount;
                    fixed (Interop.Sys.IoRingCompletion* completionsPtr = completionsBatch)
                    {
                        int result = Interop.Sys.IoRingWaitForCompletions(ring.RingHandle, completionsPtr, completionsBatch.Length, minComplete: 0, out completedCount);
                        if (result != 0)
                        {
                            int error = Marshal.GetLastPInvokeError();
                            if (new Interop.ErrorInfo(error).Error == Interop.Error.EAGAIN)
                            {
                                // Published SQEs still own their buffers. Retry without parking on
                                // eventfd: allocation failure need not generate a completion or wake.
                                Thread.Sleep(SubmissionRetryDelayMs);
                                return true;
                            }

                            Environment.FailFast($"io_uring completion wait failed: {error}.");
                        }

                        if (completedCount == 0)
                        {
                            return false;
                        }
                    }

                    processed += completedCount;
                    ReadOnlySpan<Interop.Sys.IoRingCompletion> completions = completionsBatch.AsSpan(0, completedCount);
                    Span<long> sequences = sequenceBatch.AsSpan(0, completedCount);

                    // InFlightCount tracks requests submitted but not yet finally completed - not raw
                    // completion count: a still-active multishot operation (More flag set) produces many
                    // completions from a single submission, so only its last one (More flag absent)
                    // actually retires the in-flight slot it was submitted with.
                    int finalCompletions = 0;
                    for (int i = 0; i < completions.Length; i++)
                    {
                        ref readonly Interop.Sys.IoRingCompletion completion = ref completions[i];
                        if ((completion.Flags & Interop.Sys.IoRingCompletion.More) == 0)
                        {
                            finalCompletions++;
                        }

                        // Must happen on this single issuer thread, strictly before this completion is
                        // handed off to a worker (below) - see RetainOperationToken's doc comment.
                        sequences[i] = RetainOperationToken(ring, completion.UserData);
                    }
                    ring.InFlightCount -= finalCompletions;
                    Debug.Assert(ring.InFlightCount >= 0);

                    if (s_useParallelizedEnqueue)
                    {
                        EnqueueCompletions(ring, completions, sequences);
                    }
                    else
                    {
                        DispatchBatch(ring, completions, sequences, workItemBatch);
                    }
                }

                return true;
            }

            /// <summary>
            /// Hands raw completions to workers, which resolve operations and run their callbacks.
            /// </summary>
            private static void EnqueueCompletions(Ring ring, ReadOnlySpan<Interop.Sys.IoRingCompletion> completions, ReadOnlySpan<long> sequences)
            {
                for (int i = 0; i < completions.Length; i++)
                {
                    ring.CompletionQueue.Enqueue((completions[i], sequences[i]));
                }

                ScheduleCompletionProcessing(ring);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void ScheduleCompletionProcessing(Ring ring)
            {
                if (Interlocked.CompareExchange(ref ring.CompletionProcessingRequested, 1, 0) == 0)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(ring.CompletionProcessor, preferLocal: false);
                }
            }

            /// <summary>
            /// Resolves a completion's correlation token, completes its operation, and releases the
            /// token's reference taken for this specific completion (see
            /// <see cref="RetainOperationToken"/>/<see cref="ReleaseOperationToken"/>). A multishot
            /// operation (e.g. <see cref="Interop.Sys.IoRingOp.RecvMultishot"/>) keeps producing
            /// completions - every one but the last carries
            /// <see cref="Interop.Sys.IoRingCompletion.More"/> in its
            /// <see cref="Interop.Sys.IoRingCompletion.Flags"/> - and since two of its completions can be
            /// *processed* concurrently (in either order) by independent workers even though they were
            /// *dequeued* in order, the token cannot simply be freed the instant the final (no-More)
            /// completion is seen: it is only actually freed once every completion that could ever
            /// reference it - including that final one - has finished being processed here, regardless of
            /// the order in which that happens. <paramref name="sequence"/> is this same completion's
            /// 0-based delivery sequence number (see <see cref="RetainOperationToken"/>), passed through to
            /// the operation so a multishot one can recover correct delivery order despite the above. This
            /// is the shared bookkeeping used by both <see cref="DispatchBatch"/> and
            /// <see cref="CompletionProcessorWorkItem"/>.
            /// </summary>
            private static IThreadPoolWorkItem? CompleteOperation(Ring ring, in Interop.Sys.IoRingCompletion completion, long sequence)
            {
                IIoUringOperation operation = PeekOperationToken(ring, completion.UserData);
                IThreadPoolWorkItem? workItem = operation.CompleteFromIoUring(completion.Result, completion.Flags, sequence);
                bool isFinal = (completion.Flags & Interop.Sys.IoRingCompletion.More) == 0;
                ReleaseOperationToken(ring, completion.UserData, isFinal);
                return workItem;
            }

            /// <summary>
            /// Resolves a completion's correlation token to its operation, without affecting its
            /// reference count - safe to call as long as the caller already holds (or is about to take,
            /// see <see cref="RetainOperationToken"/>) at least one outstanding reference to this token.
            /// </summary>
            private static IIoUringOperation PeekOperationToken(Ring ring, ulong userData)
            {
                if ((userData & OperationSlotTag) != 0)
                {
                    int slotIndex = (int)((uint)userData >> 1);
                    if ((uint)slotIndex >= (uint)ring.OperationSlots.Length)
                    {
                        Environment.FailFast("Invalid io_uring operation slot.");
                    }

                    ref OperationSlot slot = ref ring.OperationSlots[slotIndex];
                    IIoUringOperation? cachedOperation = Volatile.Read(ref slot.Operation);
                    uint generation = (uint)(userData >> OperationSlotGenerationShift);
                    if (cachedOperation is null || Volatile.Read(ref slot.Generation) != generation)
                    {
                        Environment.FailFast("Stale io_uring operation slot.");
                    }

                    return cachedOperation;
                }

                GCHandle handle = GCHandle.FromIntPtr((IntPtr)userData);
                return ((GCHandleToken)handle.Target!).Operation;
            }

            /// <summary>
            /// Takes one reference on <paramref name="userData"/>'s token, on behalf of a completion
            /// about to be handed off to a worker for processing (see <see cref="ReleaseOperationToken"/>
            /// for the matching release), and assigns that completion its 0-based delivery sequence
            /// number (see <see cref="OperationSlot.NextSequence"/>). Must be called by the single issuer
            /// thread that drains completions off the ring, strictly before that completion becomes
            /// visible to any worker (i.e. before it is enqueued/dispatched) - this ordering, not the
            /// reference count's atomic increment alone, is what makes the corresponding release always
            /// observe a fully-retained token, and the sequence numbers always reflect true arrival order,
            /// no matter which worker thread processes which completion first.
            /// </summary>
            private static long RetainOperationToken(Ring ring, ulong userData)
            {
                if ((userData & OperationSlotTag) != 0)
                {
                    int slotIndex = (int)((uint)userData >> 1);
                    if ((uint)slotIndex >= (uint)ring.OperationSlots.Length)
                    {
                        Environment.FailFast("Invalid io_uring operation slot.");
                    }

                    ref OperationSlot slot = ref ring.OperationSlots[slotIndex];
                    Interlocked.Increment(ref slot.RefCount);
                    return slot.NextSequence++;
                }
                else
                {
                    GCHandle handle = GCHandle.FromIntPtr((IntPtr)userData);
                    GCHandleToken token = (GCHandleToken)handle.Target!;
                    Interlocked.Increment(ref token.RefCount);
                    return token.NextSequence++;
                }
            }

            /// <summary>
            /// Releases the reference <see cref="RetainOperationToken"/> took for one completion,
            /// actually freeing the token once every reference has been released this way - i.e. once
            /// every completion this token could ever receive (including its final one, see
            /// <paramref name="isFinal"/>) has been fully processed, in any order. A final completion
            /// releases two references at once: its own (like every other completion) plus the "at least
            /// one completion is still outstanding" bias <see cref="TrySubmit"/> establishes when the
            /// token is first created - so the token is freed by whichever single release call happens to
            /// observe the count reach zero, regardless of processing order.
            /// </summary>
            private static void ReleaseOperationToken(Ring ring, ulong userData, bool isFinal)
            {
                int decrement = isFinal ? 2 : 1;
                if ((userData & OperationSlotTag) != 0)
                {
                    int slotIndex = (int)((uint)userData >> 1);
                    ref OperationSlot slot = ref ring.OperationSlots[slotIndex];
                    if (Interlocked.Add(ref slot.RefCount, -decrement) == 0)
                    {
                        Volatile.Write(ref slot.Operation, null);
                        ring.FreeOperationSlots.Enqueue(slotIndex);
                    }
                }
                else
                {
                    GCHandle handle = GCHandle.FromIntPtr((IntPtr)userData);
                    GCHandleToken token = (GCHandleToken)handle.Target!;
                    if (Interlocked.Add(ref token.RefCount, -decrement) == 0)
                    {
                        handle.Free();
                    }
                }
            }

            /// <summary>
            /// Frees <paramref name="userData"/>'s token unconditionally, bypassing the reference-counted
            /// protocol above - used only when a token is abandoned before it was ever actually submitted
            /// to the ring (see <see cref="TrySubmit"/>'s catch block), so no completion (and therefore no
            /// call to <see cref="RetainOperationToken"/>/<see cref="ReleaseOperationToken"/>) will ever
            /// reference it.
            /// </summary>
            private static void AbortOperationToken(Ring ring, ulong userData)
            {
                if ((userData & OperationSlotTag) != 0)
                {
                    int slotIndex = (int)((uint)userData >> 1);
                    Volatile.Write(ref ring.OperationSlots[slotIndex].Operation, null);
                    ring.FreeOperationSlots.Enqueue(slotIndex);
                }
                else
                {
                    GCHandle.FromIntPtr((IntPtr)userData).Free();
                }
            }

            /// <summary>
            /// Older completion hand-off path, kept only so it can still be selected (see
            /// <see cref="s_useParallelizedEnqueue"/>) for comparison against the default
            /// <see cref="EnqueueCompletions"/>/<see cref="CompletionProcessorWorkItem"/> path. Completes
            /// the operation associated with each of the given completions, collecting the (non-null)
            /// returned work items and queuing them all via a single batched
            /// <see cref="ThreadPool.UnsafeQueueUserWorkItems"/> call instead of once per completion.
            /// </summary>
            private static void DispatchBatch(Ring ring, ReadOnlySpan<Interop.Sys.IoRingCompletion> completions, ReadOnlySpan<long> sequences, IThreadPoolWorkItem[] workItemBatch)
            {
                int batchCount = 0;
                for (int i = 0; i < completions.Length; i++)
                {
                    // The issuer thread must not run the continuation inline; CompleteOperation only does
                    // minimal bookkeeping and returns the work item (if any) to be queued, so it can be
                    // batched together with the other completions drained in this pass.
                    IThreadPoolWorkItem? workItem = CompleteOperation(ring, in completions[i], sequences[i]);
                    if (workItem is not null)
                    {
                        workItemBatch[batchCount++] = workItem;
                    }
                }

                if (batchCount > 0)
                {
                    // Also wakes the normal idle-worker primitive for any parked sibling to pick these up.
                    ThreadPool.UnsafeQueueUserWorkItems(workItemBatch.AsSpan(0, batchCount), preferLocal: false);
                    Array.Clear(workItemBatch, 0, batchCount);
                }
            }

            private sealed class CompletionProcessorWorkItem : IThreadPoolWorkItem
            {
                private readonly Ring _ring;

                public CompletionProcessorWorkItem(Ring ring)
                {
                    _ring = ring;
                }

                void IThreadPoolWorkItem.Execute()
                {
                    Ring ring = _ring;
                    Thread currentThread = Thread.CurrentThread;

                    // Reset before checking the queue so racing producers cannot miss scheduling work.
                    Interlocked.Exchange(ref ring.CompletionProcessingRequested, 0);
                    if (!ring.CompletionQueue.TryDequeue(out (Interop.Sys.IoRingCompletion Completion, long Sequence) item))
                    {
                        return;
                    }
                    int startTimeMs = Environment.TickCount;
                    ScheduleCompletionProcessing(ring);
                    while (true)
                    {
                        CompleteOperation(ring, in item.Completion, item.Sequence)?.Execute();
                        // Each completion is a separate callback, even when dispatched in one work item.
                        ExecutionContext.ResetThreadPoolThread(currentThread);
                        currentThread.ResetThreadPoolThread();
                        if (Environment.TickCount - startTimeMs >= CompletionProcessorTimeSliceMs)
                        {
                            break;
                        }
                        if (!ring.CompletionQueue.TryDequeue(out item))
                        {
                            return;
                        }

                        // Dispatch accounts for the last completion when this work item returns.
                        ThreadPool.NotifyWorkItemProgress();
                    }
                    ScheduleCompletionProcessing(ring);
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
            /// Receives the raw completion result: bytes transferred on success, or <c>-errno</c>,
            /// together with the completion's raw CQE flags (e.g. <see cref="Interop.Sys.IoRingCompletion.More"/>
            /// for a still-active multishot operation, or <see cref="Interop.Sys.IoRingCompletion.Buffer"/>/
            /// <see cref="Interop.Sys.IoRingCompletion.BufferShift"/> for a selected provided-buffer id),
            /// and this completion's 0-based delivery sequence number (assigned once per completion, in
            /// true arrival order, regardless of the order in which completions of the same operation are
            /// actually *processed* by independent workers - see
            /// <see cref="PortableThreadPool.IoUringThreadPool.RetainOperationToken"/>). Operations that
            /// can only ever receive one completion (i.e. every one except a still-active multishot
            /// operation) can safely ignore <paramref name="sequence"/>, since it is always 0 for them.
            /// This may run on the issuer in legacy dispatch mode, so implementations must not invoke
            /// user continuations. Return the work item for the dispatcher to execute on a worker,
            /// or <see langword="null"/> when a partial operation has been resubmitted.
            /// </summary>
            IThreadPoolWorkItem? CompleteFromIoUring(int result, uint flags, long sequence);
        }
    }
}
