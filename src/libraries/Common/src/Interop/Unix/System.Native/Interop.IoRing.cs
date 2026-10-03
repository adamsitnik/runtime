// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static partial class Interop
{
    internal static partial class Sys
    {
        // Mirrors the native IoRingOp enum in pal_io.h.
        internal enum IoRingOp : int
        {
            Read = 0,
            Write = 1,
            ReadV = 2,
            WriteV = 3,
            Accept = 4,
            Connect = 5,
            Recv = 6,
            Send = 7,
            Cancel = 8,
            RecvMultishot = 9,
            SendMsg = 10,
            PollRead = 11,
            PollWrite = 12,
            SendZeroCopy = 15,
            Native = 16,
        }

        // Private staging copy of a 64-byte SQE, not access to a live submission queue.
        // FileDescriptor and UserData are reserved: native submission overwrites both.
        [StructLayout(LayoutKind.Sequential)]
        internal struct IoRingSubmission
        {
            public byte Opcode;
            public byte Flags;
            public ushort IoPriority;
            public int FileDescriptor;
            public ulong Offset;
            public ulong Address;
            public uint Length;
            public uint OperationFlags;
            public ulong UserData;
            public ushort BufferIndex;
            public ushort Personality;
            public int SpliceFileDescriptor;
            public ulong Address3;
            public ulong Padding;
        }

        // Mirrors the native IoRingRequest struct in pal_io.h.
        // Fd is a raw file descriptor (not a SafeHandle): the caller is responsible for keeping
        // the owning SafeHandle ref-counted/alive for as long as the request may be in flight.
        [StructLayout(LayoutKind.Explicit, Size = 80)]
        internal unsafe struct IoRingRequest
        {
            [FieldOffset(0)]
            public IoRingOp OpCode;
            [FieldOffset(4)]
            private int _fileDescriptor;
            [FieldOffset(8)]
            public ulong UserData;
            [FieldOffset(16)]
            private OperationFields _operation;
            [FieldOffset(16)]
            public IoRingSubmission NativeSubmission;

            // The descriptor is a native int on every platform; retain IntPtr at call sites.
            public IntPtr Fd
            {
                readonly get => (IntPtr)_fileDescriptor;
                set => _fileDescriptor = (int)value;
            }

            public long Offset
            {
                readonly get => _operation.Offset;
                set => _operation.Offset = value;
            }

            public byte* Buffer
            {
                readonly get => _operation.Buffer;
                set => _operation.Buffer = value;
            }

            public int BufferLength
            {
                readonly get => _operation.BufferLength;
                set => _operation.BufferLength = value;
            }

            public IOVector* Vectors
            {
                readonly get => _operation.Vectors;
                set => _operation.Vectors = value;
            }

            public int VectorCount
            {
                readonly get => _operation.VectorCount;
                set => _operation.VectorCount = value;
            }

            public int Flags
            {
                readonly get => _operation.Flags;
                set => _operation.Flags = value;
            }

            public byte* SockAddr
            {
                readonly get => _operation.SockAddr;
                set => _operation.SockAddr = value;
            }

            public int* SockAddrLen
            {
                readonly get => _operation.SockAddrLen;
                set => _operation.SockAddrLen = value;
            }

            // Sequential layout follows the runtime pointer size, including builds that do not
            // define architecture symbols. The payload overlaps NativeSubmission, not the header.
            [StructLayout(LayoutKind.Sequential)]
            private struct OperationFields
            {
                public long Offset; // -1 for non-positional ops
                public byte* Buffer; // used by Read/Write/Recv/Send; native msghdr for SendMsg
                public int BufferLength;
                public IOVector* Vectors; // used by ReadV/WriteV
                public int VectorCount;
                public int Flags; // MSG_* flags for Recv/Send; accept flags for Accept
                public byte* SockAddr; // output for Accept, input for Connect
                public int* SockAddrLen;
            }
        }

        // Mirrors the native IoRingCompletion struct in pal_io.h.
        [StructLayout(LayoutKind.Sequential)]
        internal struct IoRingCompletion
        {
            // IORING_CQE_F_MORE: this is not the final completion for the request that produced
            // it (e.g. a RecvMultishot request that is still active and will keep completing).
            public const uint More = 1 << 1;
            // IORING_CQE_F_NOTIF: releases the source buffer after a zero-copy send.
            public const uint Notification = 1 << 3;
            // IORING_CQE_F_BUFFER: Flags encodes the selected provided-buffer id, shifted left by
            // BufferShift (IORING_CQE_BUFFER_SHIFT) - only set for ops that use provided buffers
            // (RecvMultishot).
            public const uint Buffer = 1;
            public const int BufferShift = 16;

            public ulong UserData;
            public int Result;
            public uint Flags;
            public ulong Extra1;
            public ulong Extra2;
        }

        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_IoRingIsAvailable")]
        internal static partial int IoRingIsAvailable();

        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_IoRingCreateSendMessage")]
        internal static unsafe partial byte* IoRingCreateSendMessage(IntPtr socket, IOVector* vectors, int vectorCount);

        // Pass singleIssuer: 1 to request IORING_SETUP_SINGLE_ISSUER + IORING_SETUP_DEFER_TASKRUN: every
        // subsequent IoRingSubmit/IoRingWaitForCompletions call for the returned ring must then
        // come from the exact same OS thread that called this method (not merely the first thread to call
        // one of those - confirmed empirically) for the ring's whole lifetime, including
        // IoRingWaitForCompletions calls with nothing to submit; any other thread's call fails with
        // -EEXIST. Pass 0 for a plain ring that can be freely shared/rotated across threads instead.
        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_IoRingCreate", SetLastError = true)]
        internal static partial int IoRingCreate(int submissionQueueDepth, int completionQueueDepth, int singleIssuer, int flags, out SafeRingHandle ringHandle);

        internal const int IoRingCreateCqe32 = 1;

        // Cached kernel opcode support, safe to query from any thread while the ring is kept alive.
        // Not a guarantee for all flags or permission to bypass Native-path validation.
        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_IoRingIsOpcodeSupported", SetLastError = true)]
        internal static partial int IoRingIsOpcodeSupported(SafeRingHandle ringHandle, int opcode);

        // Validate the immutable staging copy before enqueueing, on any thread. Does not probe the kernel.
        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_IoRingValidateSubmission", SetLastError = true)]
        internal static partial int IoRingValidateSubmission(in IoRingSubmission submission);

        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_IoRingSubmit", SetLastError = true)]
        internal static unsafe partial int IoRingSubmit(SafeRingHandle ringHandle, IoRingRequest* requests, int requestCount, out int submittedCount);

        // Creates an eventfd and registers it with the ring (IORING_REGISTER_EVENTFD): the kernel then
        // bumps its counter whenever a CQE is posted. The returned fd is also safe for any other thread
        // to write to directly via EventFdWrite, piggybacking its own wake-up onto the same fd a single
        // waiter is blocked on in EventFdWait - see PortableThreadPool.IoUring.Unix.cs.
        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_IoRingRegisterEventFd", SetLastError = true)]
        internal static partial int IoRingRegisterEventFd(SafeRingHandle ringHandle);

        // Allocates bufferCount page-aligned buffers of bufferSize bytes each, natively owned by
        // (and freed together with) the ring, registers them as provided-buffer group zero, and
        // publishes all of them. On success, *bufferStorage points at the base of that storage
        // (buffer i occupies [bufferStorage + i * bufferSize, bufferStorage + (i + 1) * bufferSize)).
        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_IoRingRegisterBufferRing", SetLastError = true)]
        internal static unsafe partial int IoRingRegisterBufferRing(SafeRingHandle ringHandle, int bufferSize, int bufferCount, byte** bufferStorage);

        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_IoRingReturnBuffers", SetLastError = true)]
        internal static unsafe partial int IoRingReturnBuffers(SafeRingHandle ringHandle, ushort* bufferIds, int count);

        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_EventFdWrite", SetLastError = true)]
        internal static partial int EventFdWrite(int eventFd);

        // Real kernel-blocking wait (poll(2)-based - no userland spin-before-blocking), unlike
        // ManualResetEventSlim.Wait. Returns 1 if the fd became readable (and drains it), 0 on timeout.
        // Pass -1 to block indefinitely.
        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_EventFdWait", SetLastError = true)]
        internal static partial int EventFdWait(int eventFd, int timeoutMilliseconds);

        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_IoRingWaitForCompletions", SetLastError = true)]
        internal static unsafe partial int IoRingWaitForCompletions(SafeRingHandle ringHandle, IoRingCompletion* completions, int maxCompletions, int minComplete, out int completedCount);
    }
}
