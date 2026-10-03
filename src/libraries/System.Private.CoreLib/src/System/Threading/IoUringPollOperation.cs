// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading;

/// <summary>Specifies Linux descriptor-readiness events.</summary>
[Flags]
public enum IoUringPollEvents
{
    /// <summary>No events.</summary>
    None = 0,
    /// <summary>Data can be read.</summary>
    Readable = 1,
    /// <summary>Priority data is available.</summary>
    Priority = 2,
    /// <summary>Data can be written.</summary>
    Writable = 4,
    /// <summary>An error occurred.</summary>
    Error = 8,
    /// <summary>The peer hung up.</summary>
    Hangup = 16,
    /// <summary>The descriptor is invalid.</summary>
    Invalid = 32,
    /// <summary>The peer closed its sending direction.</summary>
    ReadHangup = 8192,
}

/// <summary>Produces repeated descriptor-readiness notifications without a data buffer.</summary>
[CLSCompliant(false)]
public abstract class IoUringPollOperation : IoUringMultishotOperation<IoUringPollEvents>
{
    private readonly IoUringPollEvents _events;

    /// <summary>Initializes a new instance of the <see cref="IoUringPollOperation"/> class.</summary>
    /// <param name="events">The readiness events to observe.</param>
    /// <exception cref="ArgumentOutOfRangeException">No events or unrecognized events were specified.</exception>
    protected IoUringPollOperation(IoUringPollEvents events)
    {
        const IoUringPollEvents ValidEvents = IoUringPollEvents.Readable | IoUringPollEvents.Priority |
            IoUringPollEvents.Writable | IoUringPollEvents.ReadHangup;
        if (events == IoUringPollEvents.None || (events & ~ValidEvents) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(events));
        }
        _events = events;
    }

    /// <inheritdoc/>
    protected sealed override unsafe IoUringRequest PrepareRequest() =>
        new IoUringRequest(IoUringOperationKind.PollMultishot, null, 0, flags: (int)_events);

    /// <inheritdoc/>
    protected sealed override IoUringCompletionAction ProcessCompletion(in IoUringCompletion completion)
    {
#if FEATURE_IO_URING
        if (completion.Result < 0)
        {
            return IoUringCompletionAction.Fail(IsCancellationRequested ? new OperationCanceledException(OperationCancellationToken) :
                Interop.GetExceptionForIoErrno(new Interop.ErrorInfo(-completion.Result)));
        }
        OnNext((IoUringPollEvents)completion.Result);
        return completion.HasMore ? IoUringCompletionAction.Continue :
            IoUringCompletionAction.Resubmit(PrepareRequest());
#else
        throw new PlatformNotSupportedException();
#endif
    }
}
