// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading;

/// <summary>Specifies how to continue or finish a logical io_uring operation.</summary>
[CLSCompliant(false)]
public readonly struct IoUringCompletionAction
{
    internal readonly byte _kind;
    internal readonly IoUringRequest _request;
    internal readonly Exception? _error;

    private IoUringCompletionAction(byte kind, IoUringRequest request = default, Exception? error = null)
    {
        _kind = kind;
        _request = request;
        _error = error;
    }

    /// <summary>Gets an action that finishes the logical operation, canceling and draining native work if necessary.</summary>
    public static IoUringCompletionAction Complete => default;
    /// <summary>Gets an action that waits for the next completion of the current native request.</summary>
    /// <remarks>This action is valid only for a completion whose <see cref="IoUringCompletion.HasMore"/> is <see langword="true"/>.</remarks>
    public static IoUringCompletionAction Continue => new(1);

    /// <summary>Submits another request within the same logical operation after native retirement.</summary>
    /// <param name="request">The next request.</param>
    /// <returns>An action that submits the request without replacing the cancellation registration.</returns>
    public static IoUringCompletionAction Resubmit(IoUringRequest request) => new(2, request);

    /// <summary>Finishes the operation with an error after native work has drained.</summary>
    /// <param name="error">The error to report to the final callback.</param>
    /// <returns>An action that stops the logical operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static IoUringCompletionAction Fail(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(0, error: error);
    }
}
