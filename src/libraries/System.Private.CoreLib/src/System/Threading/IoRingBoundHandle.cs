// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading;

/// <summary>Binds a handle to one experimental io_uring issuer for its lifetime.</summary>
/// <remarks>
/// Disposing the binding stops admission and requests cancellation, but does not synchronously
/// retire outstanding I/O or dispose the caller's handle. Native ownership remains protected
/// until all accepted requests retire. Bind returns the same binding for a given SafeHandle instance.
/// The owner must retain and dispose this binding when disposing its handle; disposing an
/// arbitrary SafeHandle does not notify the binding.
/// </remarks>
[CLSCompliant(false)]
public sealed partial class IoRingBoundHandle : IDisposable, IThreadPoolWorkItem
{
    /// <summary>Enqueues an operation for submission by this handle's issuer.</summary>
    /// <param name="operation">The operation whose buffers remain valid through terminal completion.</param>
    /// <param name="cancellationToken">The token that requests cancellation of this logical operation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The binding is disposed.</exception>
    /// <exception cref="InvalidOperationException">The operation is already active.</exception>
    /// <exception cref="PlatformNotSupportedException">io_uring is unavailable.</exception>
    public void EnqueueForSubmission(IoUringOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        EnqueueForSubmissionCore(operation, cancellationToken);
    }

    /// <summary>Stops admission and requests cancellation without waiting for native completion.</summary>
    /// <remarks>This method does not authorize reusing any outstanding operation's buffers.</remarks>
    public void Dispose() => DisposeCore();

    /// <summary>Disposes the binding and waits for its native requests to retire.</summary>
    /// <remarks>
    /// This does not wait for application callbacks or revoke transferred receive buffers.
    /// It allows synchronous socket close to progress without waiting for a ThreadPool worker.
    /// </remarks>
    /// <returns>Whether outstanding operations were present when disposal stopped admission.</returns>
    /// <exception cref="PlatformNotSupportedException">io_uring is unavailable.</exception>
    public bool DisposeAndWait() => DisposeAndWaitCore();

    void IThreadPoolWorkItem.Execute() => ExecuteCore();

#if !FEATURE_IO_URING
    private IoRingBoundHandle() => throw new PlatformNotSupportedException();

    private void EnqueueForSubmissionCore(IoUringOperation operation, CancellationToken cancellationToken) =>
        throw new PlatformNotSupportedException();

    private void DisposeCore()
    {
    }

    private bool DisposeAndWaitCore() => throw new PlatformNotSupportedException();

    private void ExecuteCore() => throw new PlatformNotSupportedException();
#endif
}
