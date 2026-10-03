// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Threading;

namespace System.Net.Sockets;

public abstract partial class IoUringAcceptOperation
{
    /// <inheritdoc/>
    protected sealed override unsafe IoUringRequest PrepareRequest() =>
        new IoUringRequest(IoUringOperationKind.AcceptMultishot, null, 0);

    /// <inheritdoc/>
    protected sealed override IoUringCompletionAction ProcessCompletion(in IoUringCompletion completion)
    {
        if (completion.Result < 0)
        {
            return IoUringCompletionAction.Fail(IsCancellationRequested
                ? new OperationCanceledException()
                : new SocketException((int)SocketPal.GetSocketErrorForErrorCode(new Interop.ErrorInfo(-completion.Result).Error)));
        }

        SafeSocketHandle socket;
        using (SafeHandle acceptedHandle = TakeAcceptedHandle())
        {
            socket = new SafeSocketHandle(acceptedHandle.DangerousGetHandle(), ownsHandle: true);
            acceptedHandle.SetHandleAsInvalid();
        }

        OnNext(socket);
        return completion.HasMore ? IoUringCompletionAction.Continue : IoUringCompletionAction.Resubmit(PrepareRequest());
    }
}
