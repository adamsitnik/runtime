// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Threading;

namespace System.Net.Sockets;

internal abstract class IoUringMultishotReceiveOperation : IoUringMultishotOperation<IMemoryOwner<byte>>
{
    private readonly bool _isDatagram;
    private readonly CancellationToken _cancellationToken;

    protected IoUringMultishotReceiveOperation(SocketType socketType, CancellationToken cancellationToken)
    {
        _isDatagram = socketType == SocketType.Dgram;
        _cancellationToken = cancellationToken;
    }

    protected sealed override unsafe IoUringRequest PrepareRequest() =>
        new IoUringRequest(IoUringOperationKind.ReceiveMultishot, null, 0);

    protected sealed override IoUringCompletionAction ProcessCompletion(in IoUringCompletion completion)
    {
        IMemoryOwner<byte>? buffer = TakeBuffer();
        if (completion.Result == 0 && !_isDatagram)
        {
            buffer?.Dispose();
            buffer = null;
        }
        if (buffer is null && completion.Result == 0 && _isDatagram)
        {
            buffer = EmptyDatagram.Instance;
        }
        if (buffer is not null)
        {
            OnNext(buffer);
        }
        if (completion.HasMore)
        {
            return IoUringCompletionAction.Continue;
        }
        if (completion.Result > 0 || completion.Result == 0 && _isDatagram ||
            completion.Result < 0 && new Interop.ErrorInfo(-completion.Result).Error == Interop.Error.ENOBUFS)
        {
            return IoUringCompletionAction.Resubmit(PrepareRequest());
        }
        if (completion.Result < 0)
        {
            return IoUringCompletionAction.Fail(IsCancellationRequested
                ? new OperationCanceledException(_cancellationToken)
                : new SocketException((int)SocketPal.GetSocketErrorForErrorCode(new Interop.ErrorInfo(-completion.Result).Error)));
        }
        return IoUringCompletionAction.Complete;
    }

    private sealed class EmptyDatagram : IMemoryOwner<byte>
    {
        internal static readonly EmptyDatagram Instance = new();
        public Memory<byte> Memory => Memory<byte>.Empty;
        public void Dispose() { }
    }
}
