// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading;

/// <summary>Specifies scheduling options for a native io_uring submission.</summary>
[Flags]
public enum IoUringSubmissionOptions
{
    /// <summary>Uses the kernel's default scheduling.</summary>
    None = 0,
    /// <summary>Requests asynchronous execution even when an immediate attempt is possible.</summary>
    ForceAsync = 16,
}

/// <summary>Describes the operation-specific portion of a native io_uring submission.</summary>
/// <remarks>
/// This is an unsafe interoperability contract, not access to the shared submission ring.
/// The runtime supplies the descriptor and correlation identity. All pointer targets must remain
/// valid until logical completion, including any memory-release notifications.
/// Operations that alter ring registrations, close descriptors, cancel other requests, suppress
/// completions, or require unsupported ownership protocols are rejected.
/// </remarks>
[CLSCompliant(false)]
public readonly unsafe struct IoUringSubmission
{
    /// <summary>Initializes a new instance of the <see cref="IoUringSubmission"/> struct.</summary>
    /// <param name="opcode">The Linux io_uring opcode.</param>
    /// <param name="address">The operation-specific native address.</param>
    /// <param name="length">The operation-specific nonnegative length.</param>
    /// <param name="offset">The native offset or second operation-specific address.</param>
    /// <param name="operationFlags">The native operation flags.</param>
    /// <param name="priority">The native priority or operation modifiers.</param>
    /// <param name="options">The submission scheduling options.</param>
    /// <param name="address3">The third operation-specific address.</param>
    /// <exception cref="ArgumentOutOfRangeException">The length or scheduling options are invalid.</exception>
    public IoUringSubmission(byte opcode, void* address, int length, ulong offset = 0, uint operationFlags = 0,
        ushort priority = 0, IoUringSubmissionOptions options = IoUringSubmissionOptions.None, ulong address3 = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if ((options & ~IoUringSubmissionOptions.ForceAsync) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        Opcode = opcode;
        Address = (nint)address;
        Length = length;
        Offset = offset;
        OperationFlags = operationFlags;
        Priority = priority;
        Options = options;
        Address3 = address3;
    }

    /// <summary>Gets the Linux opcode.</summary>
    public byte Opcode { get; }
    /// <summary>Gets the native address.</summary>
    public nint Address { get; }
    /// <summary>Gets the operation-specific length.</summary>
    public int Length { get; }
    /// <summary>Gets the native offset or second address.</summary>
    public ulong Offset { get; }
    /// <summary>Gets the operation-specific flags.</summary>
    public uint OperationFlags { get; }
    /// <summary>Gets the native priority or operation modifiers.</summary>
    public ushort Priority { get; }
    /// <summary>Gets the scheduling options.</summary>
    public IoUringSubmissionOptions Options { get; }
    /// <summary>Gets the third operation-specific address.</summary>
    public ulong Address3 { get; }
}
