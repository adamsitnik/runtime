// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Runtime.InteropServices;

namespace System.Threading;

/// <summary>Exposes the experimental io_uring Thread Pool infrastructure.</summary>
[CLSCompliant(false)]
public static class IoUring
{
    /// <summary>Gets a value that indicates whether io_uring is enabled and usable on this system.</summary>
    public static bool IsSupported => false;

    /// <summary>Gets the canonical binding of a handle to one io_uring issuer.</summary>
    /// <param name="handle">The handle to bind without transferring its ownership.</param>
    /// <returns>The shared binding for this handle.</returns>
    /// <exception cref="PlatformNotSupportedException">io_uring is unavailable.</exception>
    public static IoRingBoundHandle Bind(SafeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ObjectDisposedException.ThrowIf(handle.IsClosed, handle);
        if (handle.IsInvalid)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, handle);
            throw new ArgumentException(SR.Arg_InvalidHandle, nameof(handle));
        }

        throw new PlatformNotSupportedException();
    }
}

/// <summary>Binds a handle to one experimental io_uring issuer for its lifetime.</summary>
[CLSCompliant(false)]
public sealed class IoRingBoundHandle : IDisposable, IThreadPoolWorkItem
{
    internal IoRingBoundHandle() => throw new PlatformNotSupportedException();

    /// <summary>Stops admission and requests cancellation without waiting for native completion.</summary>
    public void Dispose()
    {
    }

    /// <summary>Disposes the binding and waits for its native requests to retire.</summary>
    /// <returns>Whether outstanding operations were present when disposal stopped admission.</returns>
    public bool DisposeAndWait() => throw new PlatformNotSupportedException();

    /// <summary>Enqueues an operation for submission by this handle's issuer.</summary>
    /// <param name="operation">The operation whose buffers remain valid through terminal completion.</param>
    /// <param name="cancellationToken">The token that requests cancellation of this logical operation.</param>
    public void Enqueue(IoUringOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        throw new PlatformNotSupportedException();
    }

    void IThreadPoolWorkItem.Execute() => throw new PlatformNotSupportedException();
}

/// <summary>Represents a reusable experimental io_uring operation with asynchronous cancellation.</summary>
[CLSCompliant(false)]
public abstract class IoUringOperation : IThreadPoolWorkItem
{
    /// <summary>Initializes a new instance of the <see cref="IoUringOperation"/> class.</summary>
    protected IoUringOperation()
    {
    }

    /// <summary>Gets a value that indicates whether cancellation or binding disposal was requested.</summary>
    protected bool IsCancellationRequested => throw new PlatformNotSupportedException();

    /// <summary>Gets the request to enqueue for this operation.</summary>
    protected abstract IoUringRequest Request { get; }

    /// <summary>Completes the logical operation and permits the instance to be reused.</summary>
    protected void CompleteOperation() => throw new PlatformNotSupportedException();

    /// <summary>Creates a multishot receive operation without binding or submitting it.</summary>
    /// <param name="onCompleted">The ordered callback receiving the native result, an optional owned buffer, and whether further completions will follow.</param>
    /// <returns>An operation to submit through <see cref="IoRingBoundHandle.Enqueue"/>.</returns>
    public static IoUringOperation CreateReceiveMultishot(Action<int, IMemoryOwner<byte>?, bool> onCompleted)
    {
        ArgumentNullException.ThrowIfNull(onCompleted);
        throw new PlatformNotSupportedException();
    }

    /// <summary>Enqueues a continuation of the same logical operation after native completion.</summary>
    /// <param name="request">The request for the remaining operation.</param>
    protected void EnqueueContinuation(IoUringRequest request) => throw new PlatformNotSupportedException();

    /// <summary>Receives a completion on a ThreadPool worker.</summary>
    /// <param name="result">The native result or negative errno.</param>
    /// <param name="flags">The native completion flags.</param>
    /// <param name="sequence">The completion's delivery sequence.</param>
    protected abstract void OnCompleted(int result, uint flags, long sequence);

    /// <summary>Requests cancellation of the current logical operation without waiting for completion.</summary>
    public void RequestCancellation() => throw new PlatformNotSupportedException();

    void IThreadPoolWorkItem.Execute() => throw new PlatformNotSupportedException();
}

/// <summary>Specifies an operation supported by the experimental io_uring integration.</summary>
public enum IoUringOperationKind
{
    /// <summary>Reads a file into one buffer.</summary>
    Read = 0,
    /// <summary>Writes one buffer to a file.</summary>
    Write = 1,
    /// <summary>Reads a file into native iovec entries.</summary>
    ReadScatter = 2,
    /// <summary>Writes native iovec entries to a file.</summary>
    WriteGather = 3,
    /// <summary>Accepts a socket connection.</summary>
    Accept = 4,
    /// <summary>Connects a socket.</summary>
    Connect = 5,
    /// <summary>Receives socket data.</summary>
    Receive = 6,
    /// <summary>Sends socket data.</summary>
    Send = 7,
    /// <summary>Sends socket data from native iovec entries.</summary>
    SendGather = 10,
    /// <summary>Waits for a descriptor to become readable.</summary>
    PollRead = 11,
    /// <summary>Waits for a descriptor to become writable.</summary>
    PollWrite = 12,
}

/// <summary>Describes an experimental io_uring request without its descriptor or correlation identity.</summary>
[CLSCompliant(false)]
public readonly unsafe struct IoUringRequest
{
    /// <summary>Initializes a new instance of the <see cref="IoUringRequest"/> struct.</summary>
    /// <param name="kind">One of the enumeration values that specifies the operation.</param>
    /// <param name="address">A pointer to the buffer, native iovec entries for a vectored operation, or native socket address for accept and connect.</param>
    /// <param name="length">The buffer length, or the number of iovec entries.</param>
    /// <param name="offset">The file offset, or -1 for non-positional operations.</param>
    /// <param name="flags">The native socket flags.</param>
    /// <param name="addressLength">A pointer to the native socket address length for accept and connect.</param>
    public IoUringRequest(IoUringOperationKind kind, void* address, int length, long offset = -1,
        int flags = 0, int* addressLength = null)
    {
        if (kind is < IoUringOperationKind.Read or > IoUringOperationKind.Send &&
            kind is < IoUringOperationKind.SendGather or > IoUringOperationKind.PollWrite)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(offset, -1);
        if (kind is IoUringOperationKind.ReadScatter or IoUringOperationKind.WriteGather or IoUringOperationKind.SendGather)
        {
            ArgumentOutOfRangeException.ThrowIfZero(length);
            ArgumentNullException.ThrowIfNull(address);
        }

        throw new PlatformNotSupportedException();
    }
}
