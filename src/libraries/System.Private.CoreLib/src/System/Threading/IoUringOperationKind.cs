// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading;

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
    /// <summary>Produces repeated descriptor-readiness notifications.</summary>
    PollMultishot = 13,
    /// <summary>Accepts repeated socket connections without shared address output storage.</summary>
    AcceptMultishot = 14,
    /// <summary>Sends socket data while retaining the source buffer until the kernel's release notification.</summary>
    SendZeroCopy = 15,
}
