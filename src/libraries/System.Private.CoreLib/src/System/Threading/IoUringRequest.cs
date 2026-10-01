// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading;

/// <summary>Describes an experimental io_uring request without its descriptor or correlation identity.</summary>
/// <remarks>
/// All pointed-to storage must remain valid until the operation receives its terminal completion.
/// Requesting cancellation or disposing the binding does not end that lifetime.
/// </remarks>
[CLSCompliant(false)]
public readonly unsafe partial struct IoUringRequest
{
    /// <summary>Initializes a new instance of the <see cref="IoUringRequest"/> struct.</summary>
    /// <param name="kind">One of the enumeration values that specifies the operation.</param>
    /// <param name="address">A pointer to the buffer, native iovec entries for a vectored operation, or native socket address for accept and connect.</param>
    /// <param name="length">The buffer length, or the number of iovec entries. Ignored for accept and connect.</param>
    /// <param name="offset">The file offset, or -1 for non-positional operations.</param>
    /// <param name="flags">The native socket flags.</param>
    /// <param name="addressLength">A pointer to the native socket address length for accept and connect. For accept, the value specifies the address capacity on input and receives the address length on completion.</param>
    /// <exception cref="ArgumentOutOfRangeException">The operation, length, or offset is invalid.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is null for a vectored operation.</exception>
    /// <exception cref="PlatformNotSupportedException">io_uring is unavailable on this platform.</exception>
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

#if FEATURE_IO_URING
        _nativeRequest = CreateNativeRequest(kind, address, length, offset, flags, addressLength);
#else
        throw new PlatformNotSupportedException();
#endif
    }
}
