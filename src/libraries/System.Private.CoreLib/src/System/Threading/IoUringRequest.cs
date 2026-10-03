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
#if FEATURE_IO_URING
    internal readonly Interop.Sys.IoRingRequest _nativeRequest;
#endif
    internal readonly ReadOnlyMemory<byte> _buffer;
    internal readonly bool _hasBuffer;
    internal readonly int _bufferOffset;

    /// <summary>Creates a request from an immutable, validated native submission description.</summary>
    /// <param name="submission">The operation-specific native fields, without a descriptor or correlation identity.</param>
    /// <returns>A request that must be submitted through a bound handle.</returns>
    /// <remarks>
    /// Pointer ownership remains the author's responsibility. Validation restricts operations to the
    /// completion and ownership protocols supported by this runtime; kernel opcode support alone
    /// does not imply that a submission is permitted.
    /// </remarks>
    /// <exception cref="ArgumentException">The submission violates the shared ring's ownership or completion protocol.</exception>
    /// <exception cref="PlatformNotSupportedException">The native operation is not supported by this runtime.</exception>
    public static IoUringRequest CreateUnsafe(in IoUringSubmission submission)
    {
#if FEATURE_IO_URING
        Interop.Sys.IoRingSubmission nativeSubmission = new()
        {
            Opcode = submission.Opcode,
            Flags = (byte)submission.Options,
            IoPriority = submission.Priority,
            Offset = submission.Offset,
            Address = (ulong)submission.Address,
            Length = (uint)submission.Length,
            OperationFlags = submission.OperationFlags,
            Address3 = submission.Address3,
        };
        if (Interop.Sys.IoRingValidateSubmission(in nativeSubmission) != 0)
        {
            Interop.ErrorInfo error = Interop.Sys.GetLastErrorInfo();
            if (error.Error is Interop.Error.ENOTSUP or Interop.Error.ENOSYS)
            {
                throw new PlatformNotSupportedException();
            }
            throw new ArgumentException(SR.Arg_IoUringInvalidSubmission, nameof(submission),
                Interop.GetExceptionForIoErrno(error));
        }
        Interop.Sys.IoRingRequest request = new()
        {
            OpCode = Interop.Sys.IoRingOp.Native,
            NativeSubmission = nativeSubmission,
        };
        return new IoUringRequest(in request);
#else
        throw new PlatformNotSupportedException();
#endif
    }

    /// <summary>Initializes a request whose managed buffer is pinned by the runtime.</summary>
    /// <param name="kind">A scalar read, write, receive, or send operation.</param>
    /// <param name="buffer">The buffer borrowed until logical completion.</param>
    /// <param name="offset">The file offset, or -1 for non-positional operations.</param>
    /// <param name="flags">The native operation flags.</param>
    /// <exception cref="ArgumentOutOfRangeException">The kind or offset is invalid.</exception>
    /// <exception cref="ArgumentException">An ordinary send requests socket error-queue zero-copy notifications.</exception>
    /// <exception cref="PlatformNotSupportedException">io_uring is unavailable.</exception>
    public IoUringRequest(IoUringOperationKind kind, Memory<byte> buffer, long offset = -1, int flags = 0)
        : this(kind, (ReadOnlyMemory<byte>)buffer, offset, flags, writable: true)
    {
    }

    /// <summary>Initializes a request that reads from a managed source buffer pinned by the runtime.</summary>
    /// <param name="kind">A scalar write or send operation.</param>
    /// <param name="buffer">The source buffer borrowed until logical completion.</param>
    /// <param name="offset">The file offset, or -1 for non-positional operations.</param>
    /// <param name="flags">The native operation flags.</param>
    /// <exception cref="ArgumentOutOfRangeException">The kind or offset is invalid.</exception>
    /// <exception cref="ArgumentException">An ordinary send requests socket error-queue zero-copy notifications.</exception>
    /// <exception cref="PlatformNotSupportedException">io_uring is unavailable.</exception>
    public IoUringRequest(IoUringOperationKind kind, ReadOnlyMemory<byte> buffer, long offset = -1, int flags = 0)
        : this(kind, buffer, offset, flags, writable: false)
    {
    }

    private IoUringRequest(IoUringOperationKind kind, ReadOnlyMemory<byte> buffer, long offset, int flags, bool writable)
    {
        this = default;
        if (kind is not (IoUringOperationKind.Write or IoUringOperationKind.Send or IoUringOperationKind.SendZeroCopy) &&
            !(writable && kind is IoUringOperationKind.Read or IoUringOperationKind.Receive))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(offset, -1);
        ValidateFlags(kind, flags);
#if FEATURE_IO_URING
        _nativeRequest = CreateNativeRequest(kind, null, buffer.Length, offset, flags, null);
        _buffer = buffer;
        _hasBuffer = true;
#else
        throw new PlatformNotSupportedException();
#endif
    }

    /// <summary>Creates a continuation over a slice of this request's managed buffer without acquiring another pin.</summary>
    /// <param name="offset">The offset within the current buffer window.</param>
    /// <param name="length">The new window length.</param>
    /// <returns>A request sharing the original managed buffer.</returns>
    /// <exception cref="InvalidOperationException">This request does not use a managed buffer.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The window lies outside the current buffer.</exception>
    public IoUringRequest SliceBuffer(int offset, int length)
    {
#if FEATURE_IO_URING
        if (!_hasBuffer)
        {
            throw new InvalidOperationException(SR.InvalidOperation_IoUringManagedBufferRequired);
        }
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, _nativeRequest.BufferLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, _nativeRequest.BufferLength - offset);
        Interop.Sys.IoRingRequest request = _nativeRequest;
        request.BufferLength = length;
        return new IoUringRequest(in request, _buffer, _bufferOffset + offset);
#else
        throw new PlatformNotSupportedException();
#endif
    }

    /// <summary>Creates a request with a different file offset while retaining its buffer.</summary>
    /// <param name="offset">The file offset, or -1 for non-positional operations.</param>
    /// <returns>The updated request.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is less than -1.</exception>
    /// <exception cref="InvalidOperationException">The request was created from a native submission.</exception>
    public IoUringRequest WithOffset(long offset)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(offset, -1);
#if FEATURE_IO_URING
        if (_nativeRequest.OpCode == Interop.Sys.IoRingOp.Native)
        {
            throw new InvalidOperationException(SR.InvalidOperation_IoUringNativeOffset);
        }
        Interop.Sys.IoRingRequest request = _nativeRequest;
        request.Offset = offset;
        return _hasBuffer ? new IoUringRequest(in request, _buffer, _bufferOffset) : new IoUringRequest(in request);
#else
        throw new PlatformNotSupportedException();
#endif
    }

    /// <summary>Initializes a new instance of the <see cref="IoUringRequest"/> struct.</summary>
    /// <param name="kind">One of the enumeration values that specifies the operation.</param>
    /// <param name="address">A pointer to the buffer, native iovec entries for a vectored operation, or native socket address for accept and connect.</param>
    /// <param name="length">The buffer length, or the number of iovec entries. Ignored for accept and connect.</param>
    /// <param name="offset">The file offset, or -1 for non-positional operations.</param>
    /// <param name="flags">The native socket flags.</param>
    /// <param name="addressLength">A pointer to the native socket address length for accept and connect. For accept, the value specifies the address capacity on input and receives the address length on completion.</param>
    /// <exception cref="ArgumentOutOfRangeException">The operation, length, or offset is invalid.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is null for a vectored operation.</exception>
    /// <exception cref="ArgumentException">An ordinary send requests socket error-queue zero-copy notifications.</exception>
    /// <exception cref="PlatformNotSupportedException">io_uring is unavailable on this platform.</exception>
    public IoUringRequest(IoUringOperationKind kind, void* address, int length, long offset = -1,
        int flags = 0, int* addressLength = null)
    {
        this = default;
        if (kind is < IoUringOperationKind.Read or > IoUringOperationKind.Send &&
            kind is < IoUringOperationKind.SendGather or > IoUringOperationKind.SendZeroCopy)
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
        ValidateFlags(kind, flags);

#if FEATURE_IO_URING
        _nativeRequest = CreateNativeRequest(kind, address, length, offset, flags, addressLength);
#else
        throw new PlatformNotSupportedException();
#endif
    }

    private static void ValidateFlags(IoUringOperationKind kind, int flags)
    {
        // Ordinary MSG_ZEROCOPY sends release storage through the socket error queue, not their CQE.
        const int MsgZeroCopy = 0x04000000;
        if ((flags & MsgZeroCopy) != 0 && kind is IoUringOperationKind.Send or IoUringOperationKind.SendGather)
        {
            throw new ArgumentException(SR.Arg_IoUringZeroCopySendFlags, nameof(flags));
        }
    }
}
