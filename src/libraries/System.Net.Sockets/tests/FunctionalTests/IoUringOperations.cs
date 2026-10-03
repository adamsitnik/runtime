// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.IO;
using System.Threading;

namespace System.Net.Sockets.Tests;

// These examples deliberately use only public APIs. The same source can be compiled
// outside the runtime test project, without its friend-assembly access.
[Flags]
internal enum IoUringPollEvents
{
    Readable = 1,
    Writable = 4,
}

internal sealed class TestPollOperation : IoUringMultishotOperation<IoUringPollEvents>
{
    private readonly IoUringPollEvents _events;
    private readonly Action<IoUringPollEvents> _onNext;
    private readonly Action<Exception?> _completed;

    public TestPollOperation(IoUringPollEvents events, Action<IoUringPollEvents> onNext, Action<Exception?> completed)
    {
        _events = events;
        _onNext = onNext;
        _completed = completed;
    }

    internal static unsafe IoUringRequest CreateRequest(IoUringPollEvents events)
    {
        const byte PollAddOpcode = 6;
        const int PollAddMulti = 1;
        uint nativeEvents = (uint)events;
        if (!BitConverter.IsLittleEndian)
        {
            nativeEvents = (nativeEvents << 16) | (nativeEvents >> 16);
        }
        IoUringSubmission submission = new(PollAddOpcode, null, PollAddMulti, operationFlags: nativeEvents);
        return IoUringRequest.CreateUnsafe(in submission);
    }

    protected override IoUringRequest PrepareRequest() => CreateRequest(_events);

    protected override IoUringCompletionAction ProcessCompletion(in IoUringCompletion completion)
    {
        if (completion.Result < 0)
        {
            return IoUringCompletionAction.Fail(IsCancellationRequested
                ? new OperationCanceledException() : new IOException($"Poll failed with errno {-completion.Result}."));
        }
        OnNext((IoUringPollEvents)completion.Result);
        return completion.HasMore ? IoUringCompletionAction.Continue : IoUringCompletionAction.Resubmit(PrepareRequest());
    }

    protected override void OnNext(IoUringPollEvents result) => _onNext(result);
    protected override void OnCompleted(Exception? error) => _completed(error);
}

internal sealed class TestAcceptOperation : IoUringMultishotOperation<SafeSocketHandle>
{
    private readonly Action<SafeSocketHandle> _onNext;
    private readonly Action<Exception?> _completed;

    public TestAcceptOperation(Action<SafeSocketHandle> onNext, Action<Exception?> completed)
    {
        _onNext = onNext;
        _completed = completed;
    }

    internal static unsafe IoUringRequest CreateRequest()
    {
        const byte AcceptOpcode = 13;
        const ushort AcceptMultishot = 1;
        IoUringSubmission submission = new(AcceptOpcode, null, 0, priority: AcceptMultishot);
        return IoUringRequest.CreateUnsafe(in submission);
    }

    protected override IoUringRequest PrepareRequest() => CreateRequest();

    protected override IoUringCompletionAction ProcessCompletion(in IoUringCompletion completion)
    {
        if (completion.Result < 0)
        {
            return IoUringCompletionAction.Fail(IsCancellationRequested
                ? new OperationCanceledException() : new IOException($"Accept failed with errno {-completion.Result}."));
        }
        // Ownership transfers to the consumer at callback entry, including when it throws.
        OnNext(new SafeSocketHandle((IntPtr)completion.Result, ownsHandle: true));
        return completion.HasMore ? IoUringCompletionAction.Continue : IoUringCompletionAction.Resubmit(PrepareRequest());
    }

    internal static void Discard(IoUringCompletion completion)
    {
        if (completion.Result >= 0)
        {
            using SafeSocketHandle handle = new((IntPtr)completion.Result, ownsHandle: true);
        }
    }

    protected override void OnCompletionDiscarded(in IoUringCompletion completion) => Discard(completion);
    protected override void OnNext(SafeSocketHandle result) => _onNext(result);
    protected override void OnCompleted(Exception? error) => _completed(error);
}

internal sealed class TestReceiveOperation : IoUringMultishotOperation<IMemoryOwner<byte>>
{
    private readonly Action<int, IMemoryOwner<byte>?, bool> _callback;
    private int _nativeError;

    public TestReceiveOperation(Action<int, IMemoryOwner<byte>?, bool> callback) => _callback = callback;

    protected override unsafe IoUringRequest PrepareRequest() =>
        new IoUringRequest(IoUringOperationKind.ReceiveMultishot, null, 0);

    protected override IoUringCompletionAction ProcessCompletion(in IoUringCompletion completion)
    {
        IMemoryOwner<byte>? buffer = TakeBuffer();
        if (buffer is not null)
        {
            if (completion.Result > 0)
            {
                OnNext(buffer);
            }
            else
            {
                buffer.Dispose();
            }
        }
        if (completion.HasMore)
        {
            return IoUringCompletionAction.Continue;
        }
        const int NoBuffers = 105; // Linux ENOBUFS.
        if (completion.Result > 0 || completion.Result == -NoBuffers)
        {
            return IoUringCompletionAction.Resubmit(PrepareRequest());
        }
        if (completion.Result < 0)
        {
            _nativeError = -completion.Result;
            return IoUringCompletionAction.Fail(IsCancellationRequested
                ? new OperationCanceledException() : new IOException($"Receive failed with errno {_nativeError}."));
        }
        return IoUringCompletionAction.Complete;
    }

    protected override void OnNext(IMemoryOwner<byte> result) => _callback(result.Memory.Length, result, true);

    protected override void OnCompleted(Exception? error)
    {
        int result = error is OperationCanceledException or ObjectDisposedException ? -125 :
            error is OutOfMemoryException ? -12 : -_nativeError;
        _nativeError = 0;
        if (error is not null && result == 0)
        {
            throw error;
        }
        _callback(result, null, false);
    }
}
