// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading;

/// <summary>Describes one native io_uring completion without assigning a meaning to its result.</summary>
[CLSCompliant(false)]
public readonly struct IoUringCompletion
{
    internal IoUringCompletion(int result, uint flags, ulong extra1 = 0, ulong extra2 = 0)
    {
        Result = result;
        Flags = flags;
        Extra1 = extra1;
        Extra2 = extra2;
    }

    /// <summary>Gets the native result, or a negative errno on failure.</summary>
    public int Result { get; }
    /// <summary>Gets the native completion flags.</summary>
    public uint Flags { get; }
    /// <summary>Gets the first operation-specific word of an extended completion, or zero for an ordinary completion.</summary>
    public ulong Extra1 { get; }
    /// <summary>Gets the second operation-specific word of an extended completion, or zero for an ordinary completion.</summary>
    public ulong Extra2 { get; }
    /// <summary>Gets a value that indicates whether the native request can produce further completions.</summary>
    public bool HasMore => (Flags & 2) != 0; // IORING_CQE_F_MORE
    /// <summary>Gets a value that indicates whether this is a memory-release notification.</summary>
    public bool IsNotification => (Flags & 8) != 0; // IORING_CQE_F_NOTIF
}
