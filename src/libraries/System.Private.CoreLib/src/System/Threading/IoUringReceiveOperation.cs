// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace System.Threading;

/// <summary>Produces independently owned buffers from a multishot socket receive.</summary>
/// <remarks>Buffer exhaustion pauses reception until a consumer returns a lease. Cancellation does not revoke delivered leases.</remarks>
[CLSCompliant(false)]
public abstract class IoUringReceiveOperation : IoUringMultishotOperation<IMemoryOwner<byte>>
{
#if FEATURE_IO_URING
    private bool _isDatagram;
#endif

    /// <summary>Initializes a new instance of the <see cref="IoUringReceiveOperation"/> class.</summary>
    protected IoUringReceiveOperation()
    {
    }

    /// <inheritdoc/>
    protected sealed override IoUringRequest PrepareRequest()
    {
#if FEATURE_IO_URING
        if (_binding!._ring.ReceiveBuffers is null)
        {
            throw new PlatformNotSupportedException();
        }
        const int DatagramSocketType = 2;
        _isDatagram = _binding.GetSocketType() == DatagramSocketType;
        return CreateRequest();
#else
        throw new PlatformNotSupportedException();
#endif
    }

    /// <inheritdoc/>
    protected sealed override IoUringCompletionAction ProcessCompletion(in IoUringCompletion completion)
    {
#if FEATURE_IO_URING
        IMemoryOwner<byte>? buffer = TakeProvidedBuffer(in completion, _isDatagram);
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
            return IoUringCompletionAction.Resubmit(CreateRequest());
        }
        if (completion.Result < 0)
        {
            return IoUringCompletionAction.Fail(IsCancellationRequested
                ? new OperationCanceledException(OperationCancellationToken) : CreateException(-completion.Result));
        }
        return IoUringCompletionAction.Complete;
#else
        throw new PlatformNotSupportedException();
#endif
    }

    /// <summary>Creates the exception for a failed native receive.</summary>
    /// <param name="errorCode">The positive native errno.</param>
    /// <returns>The exception to report through logical completion.</returns>
    protected virtual Exception CreateException(int errorCode)
    {
#if FEATURE_IO_URING
        return Interop.GetExceptionForIoErrno(new Interop.ErrorInfo(errorCode));
#else
        throw new PlatformNotSupportedException();
#endif
    }

#if FEATURE_IO_URING
    private static IoUringRequest CreateRequest()
    {
        Interop.Sys.IoRingRequest request = default;
        request.OpCode = Interop.Sys.IoRingOp.RecvMultishot;
        request.Offset = -1;
        return new IoUringRequest(in request);
    }

    private sealed class EmptyDatagram : IMemoryOwner<byte>
    {
        internal static readonly EmptyDatagram Instance = new();
        public Memory<byte> Memory => Memory<byte>.Empty;
        public void Dispose() { }
    }
#endif
}
