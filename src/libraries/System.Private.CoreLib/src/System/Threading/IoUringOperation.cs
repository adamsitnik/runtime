// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading;

/// <summary>Represents a reusable experimental io_uring operation with asynchronous cancellation.</summary>
/// <remarks>
/// An instance supports one logical operation at a time. The runtime retains request resources until
/// native completion and performs logical cleanup before invoking <see cref="OnCompleted"/>.
/// Completion callbacks run on ThreadPool workers and must not invoke the work-item interface directly.
/// </remarks>
[CLSCompliant(false)]
public abstract partial class IoUringOperation : IThreadPoolWorkItem
{
    /// <summary>Initializes a new instance of the <see cref="IoUringOperation"/> class.</summary>
    protected IoUringOperation()
    {
    }

    /// <summary>Prepares the first request of a logical operation.</summary>
    /// <returns>The request to submit through the binding.</returns>
    /// <remarks>Called after exclusive use of this instance has been acquired, before native publication.</remarks>
    protected abstract IoUringRequest PrepareRequest();

    /// <summary>Gets a value that indicates whether cancellation or binding disposal was requested.</summary>
    protected bool IsCancellationRequested => IsCancellationRequestedCore;

    /// <summary>Requests cancellation of the current logical operation without waiting for completion.</summary>
    /// <remarks>
    /// The request can race successful completion. It never releases buffers or revokes delivered data.
    /// Do not retain this method as a cancellation capability after the instance has been reused.
    /// </remarks>
    public void RequestCancellation() => RequestCancellationCore();

    /// <summary>Processes a native completion and selects the next step of the logical operation.</summary>
    /// <param name="completion">The native completion, including its result or negative errno.</param>
    /// <returns>The action for the runtime to perform after this method returns.</returns>
    /// <remarks>
    /// Calls are ordered and nonconcurrent within a logical use. Throwing stops delivery and requests
    /// cancellation; the exception is reported to <see cref="OnCompleted"/> after native drain.
    /// Native errors are passed to this method without translation.
    /// </remarks>
    protected abstract IoUringCompletionAction ProcessCompletion(in IoUringCompletion completion);

    /// <summary>Releases operation-specific resources belonging to a completion that is not delivered.</summary>
    /// <param name="completion">A completion received after processing stopped.</param>
    /// <remarks>
    /// After <see cref="ProcessCompletion"/> throws or requests completion, cancellation can still
    /// race successful native completions. This callback runs in order for those remaining completions,
    /// before <see cref="OnCompleted"/>. Implementations of unsafe requests must release any resources
    /// produced by them. Resources belonging to a delivered completion remain the implementation's
    /// responsibility, including when <see cref="ProcessCompletion"/> throws.
    /// Runtime-provided buffers are returned automatically and must not be released here.
    /// </remarks>
    protected virtual void OnCompletionDiscarded(in IoUringCompletion completion)
    {
    }

    /// <summary>Releases application reservations before the runtime releases request buffers.</summary>
    /// <remarks>
    /// Called after native retirement, while the logical operation is still active. This callback
    /// also runs when an initial submission is rolled back; in that case <see cref="OnCompleted"/>
    /// is not called. Overrides must not release runtime-owned pins or reuse the operation.
    /// </remarks>
    protected virtual void OnCompleting()
    {
    }

    /// <summary>Notifies the implementation that the logical operation has finished.</summary>
    /// <param name="error">An error from processing, continuation submission, or resource cleanup; otherwise, <see langword="null"/>.</param>
    /// <remarks>
    /// Runtime-owned pins and cancellation state have been released before this callback. The instance
    /// can be reused from this callback. Exceptions escaping this callback follow the ThreadPool's
    /// unhandled-exception policy. Initial submission failures are thrown to the submitter instead.
    /// </remarks>
    protected abstract void OnCompleted(Exception? error);

    /// <summary>Takes ownership of the runtime-provided buffer for the current completion.</summary>
    /// <returns>A buffer lease, or <see langword="null"/> if the completion has no buffer.</returns>
    /// <remarks>
    /// Call only from <see cref="ProcessCompletion"/>. Each completion's buffer can be taken once.
    /// The lease remains valid after cancellation or logical completion and must be disposed by its
    /// recipient. Untaken buffers are returned automatically when the callback exits.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The caller is outside the completion callback or already took the buffer.</exception>
    protected System.Buffers.IMemoryOwner<byte>? TakeBuffer() => TakeBufferCore();

    void IThreadPoolWorkItem.Execute() => ExecuteCore();
}
