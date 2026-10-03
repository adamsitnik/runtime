// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading;

/// <summary>Represents a scalar io_uring operation with a runtime-pinned managed buffer.</summary>
[CLSCompliant(false)]
public abstract class IoUringBufferOperation : IoUringOperation
{
    /// <summary>Initializes an operation with a writable buffer.</summary>
    /// <param name="kind">The scalar operation kind.</param>
    /// <param name="buffer">The buffer borrowed until logical completion.</param>
    /// <param name="offset">The file offset, or -1 for non-positional operations.</param>
    /// <param name="flags">The native operation flags.</param>
    protected IoUringBufferOperation(IoUringOperationKind kind, Memory<byte> buffer, long offset = -1, int flags = 0)
    {
        Request = new IoUringRequest(kind, buffer, offset, flags);
    }

    /// <summary>Initializes an operation with a read-only source buffer.</summary>
    /// <param name="kind">The scalar write or send operation kind.</param>
    /// <param name="buffer">The source buffer borrowed until logical completion.</param>
    /// <param name="offset">The file offset, or -1 for non-positional operations.</param>
    /// <param name="flags">The native operation flags.</param>
    protected IoUringBufferOperation(IoUringOperationKind kind, ReadOnlyMemory<byte> buffer, long offset = -1, int flags = 0)
    {
        Request = new IoUringRequest(kind, buffer, offset, flags);
    }

    /// <summary>Gets the initial request, which can be sliced to describe partial-I/O continuations.</summary>
    protected IoUringRequest Request { get; }

    /// <inheritdoc/>
    protected sealed override IoUringRequest PrepareRequest() => Request;
}
