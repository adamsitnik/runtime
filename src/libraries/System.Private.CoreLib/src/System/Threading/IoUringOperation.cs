// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace System.Threading;

/// <summary>Represents a reusable experimental io_uring operation with asynchronous cancellation.</summary>
/// <remarks>
/// An instance supports one logical operation at a time. Derived implementations retain their
/// buffers until terminal completion, then call <see cref="CompleteOperation"/> before notifying
/// their caller or caching the instance. Completion callbacks run on ThreadPool workers.
/// </remarks>
[CLSCompliant(false)]
public abstract partial class IoUringOperation : IThreadPoolWorkItem
{
    /// <summary>Initializes a new instance of the <see cref="IoUringOperation"/> class.</summary>
    protected IoUringOperation()
    {
    }

    /// <summary>Creates a multishot receive operation without binding or submitting it.</summary>
    /// <param name="onCompleted">The ordered callback receiving the native result, an optional owned buffer, and whether further completions will follow.</param>
    /// <returns>An operation to submit through <see cref="IoRingBoundHandle.EnqueueForSubmission"/>.</returns>
    /// <remarks>
    /// Callbacks run nonconcurrently on ThreadPool workers. The result is the number of bytes received,
    /// zero on stream EOF or an empty datagram, or a negative errno on failure. Empty datagrams have
    /// an empty, non-null buffer and do not end the operation.
    /// The callback owns each delivered buffer and must dispose it when finished, even if the callback throws.
    /// The final callback reports no further completions. Native requests may be rearmed transparently,
    /// and reception waits when all provided buffers are retained until a consumer returns a buffer.
    /// Cancellation and binding disposal stop the operation without revoking delivered buffers.
    /// The operation supports one logical receive at a time and may be reused after its final callback begins.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="onCompleted"/> is null.</exception>
    /// <exception cref="PlatformNotSupportedException">io_uring is unavailable on this platform.</exception>
    public static IoUringOperation CreateReceiveMultishot(Action<int, IMemoryOwner<byte>?, bool> onCompleted)
    {
        ArgumentNullException.ThrowIfNull(onCompleted);
#if FEATURE_IO_URING
        return new PortableThreadPool.IoUringThreadPool.MultishotReceiveOperation(onCompleted);
#else
        throw new PlatformNotSupportedException();
#endif
    }

    /// <summary>Gets the request to enqueue for this operation.</summary>
    protected abstract IoUringRequest Request { get; }

    /// <summary>Gets a value that indicates whether cancellation or binding disposal was requested.</summary>
    protected bool IsCancellationRequested => IsCancellationRequestedCore;

    /// <summary>Requests cancellation of the current logical operation without waiting for completion.</summary>
    /// <remarks>
    /// The request can race successful completion. It never releases buffers or revokes delivered data.
    /// Do not retain this method as a cancellation capability after the instance has been reused.
    /// </remarks>
    public void RequestCancellation() => RequestCancellationCore();

    /// <summary>Receives a completion on a ThreadPool worker.</summary>
    /// <param name="result">The native result or negative errno.</param>
    /// <param name="flags">The native completion flags.</param>
    /// <param name="sequence">The completion's delivery sequence.</param>
    protected abstract void OnCompleted(int result, uint flags, long sequence);

    /// <summary>Completes the logical operation and permits the instance to be reused.</summary>
    /// <remarks>
    /// Call this only after native completion and before publishing the final result to the caller.
    /// This method waits for an executing cancellation-token callback, not for native I/O.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A native request is still pending.</exception>
    protected void CompleteOperation() => CompleteOperationCore();

    /// <summary>Enqueues a continuation of the same logical operation after native completion.</summary>
    /// <param name="request">The request for the remaining operation.</param>
    /// <remarks>The original cancellation registration remains active across the continuation.</remarks>
    protected void EnqueueContinuation(IoUringRequest request) => EnqueueContinuationCore(request);

    void IThreadPoolWorkItem.Execute() => ExecuteCore();

#if !FEATURE_IO_URING
    private bool IsCancellationRequestedCore => throw new PlatformNotSupportedException();

    private void RequestCancellationCore() => throw new PlatformNotSupportedException();

    private void CompleteOperationCore() => throw new PlatformNotSupportedException();

    private void EnqueueContinuationCore(IoUringRequest request) => throw new PlatformNotSupportedException();

    private void ExecuteCore() => throw new PlatformNotSupportedException();
#endif
}
