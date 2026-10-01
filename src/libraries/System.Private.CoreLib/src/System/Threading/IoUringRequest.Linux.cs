// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading;

public readonly unsafe partial struct IoUringRequest
{
    internal readonly Interop.Sys.IoRingRequest _nativeRequest;

    internal IoUringRequest(in Interop.Sys.IoRingRequest request) => _nativeRequest = request;

    private static Interop.Sys.IoRingRequest CreateNativeRequest(IoUringOperationKind kind,
        void* address, int length, long offset, int flags, int* addressLength)
    {
        Interop.Sys.IoRingRequest request = default;
        request.OpCode = (Interop.Sys.IoRingOp)kind;
        request.Offset = offset;
        request.BufferLength = length;
        request.Flags = flags;
        if (kind is IoUringOperationKind.Accept or IoUringOperationKind.Connect)
        {
            request.SockAddr = (byte*)address;
            request.SockAddrLen = addressLength;
        }
        else if (kind is IoUringOperationKind.ReadScatter or IoUringOperationKind.WriteGather)
        {
            request.Vectors = (Interop.Sys.IOVector*)address;
            request.VectorCount = length;
        }
        else
        {
            request.Buffer = (byte*)address;
        }

        return request;
    }
}
