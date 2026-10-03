// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// ------------------------------------------------------------------------------
// Changes to this file must follow the https://aka.ms/api-review process.
// ------------------------------------------------------------------------------

namespace System.Threading
{
    [System.CLSCompliantAttribute(false)]
    public sealed partial class IoRingBoundHandle : System.IDisposable, System.Threading.IThreadPoolWorkItem
    {
        internal IoRingBoundHandle() { }
        public void Dispose() { }
        public bool DisposeAndWait() { throw null; }
        public void EnqueueForSubmission(System.Threading.IoUringOperation operation, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) { }
        public bool IsOperationSupported(byte opcode) { throw null; }
        void System.Threading.IThreadPoolWorkItem.Execute() { }
    }
    [System.CLSCompliantAttribute(false)]
    public static partial class IoUring
    {
        public static bool IsSupported { get { throw null; } }
        public static System.Threading.IoRingBoundHandle Bind(System.Runtime.InteropServices.SafeHandle handle, bool ownsFileDescriptor) { throw null; }
    }
    [System.CLSCompliantAttribute(false)]
    public abstract partial class IoUringBufferOperation : System.Threading.IoUringOperation
    {
        protected IoUringBufferOperation(System.Threading.IoUringOperationKind kind, System.Memory<byte> buffer, long offset = (long)-1, int flags = 0) { }
        protected IoUringBufferOperation(System.Threading.IoUringOperationKind kind, System.ReadOnlyMemory<byte> buffer, long offset = (long)-1, int flags = 0) { }
        protected System.Threading.IoUringRequest Request { get { throw null; } }
        protected sealed override System.Threading.IoUringRequest PrepareRequest() { throw null; }
    }
    [System.CLSCompliantAttribute(false)]
    public readonly partial struct IoUringCompletion
    {
        private readonly int _dummyPrimitive;
        public ulong Extra1 { get { throw null; } }
        public ulong Extra2 { get { throw null; } }
        public uint Flags { get { throw null; } }
        public bool HasMore { get { throw null; } }
        public bool IsNotification { get { throw null; } }
        public int Result { get { throw null; } }
    }
    [System.CLSCompliantAttribute(false)]
    public readonly partial struct IoUringCompletionAction
    {
        private readonly object _dummy;
        private readonly int _dummyPrimitive;
        public static System.Threading.IoUringCompletionAction Complete { get { throw null; } }
        public static System.Threading.IoUringCompletionAction Continue { get { throw null; } }
        public static System.Threading.IoUringCompletionAction Fail(System.Exception error) { throw null; }
        public static System.Threading.IoUringCompletionAction Resubmit(System.Threading.IoUringRequest request) { throw null; }
    }
    [System.CLSCompliantAttribute(false)]
    public abstract partial class IoUringMultishotOperation<T> : System.Threading.IoUringOperation
    {
        protected IoUringMultishotOperation() { }
        protected abstract void OnNext(T result);
    }
    [System.CLSCompliantAttribute(false)]
    public abstract partial class IoUringOperation : System.Threading.IThreadPoolWorkItem
    {
        protected IoUringOperation() { }
        protected bool IsCancellationRequested { get { throw null; } }
        protected abstract void OnCompleted(System.Exception? error);
        protected virtual void OnCompleting() { }
        protected abstract System.Threading.IoUringRequest PrepareRequest();
        protected abstract System.Threading.IoUringCompletionAction ProcessCompletion(in System.Threading.IoUringCompletion completion);
        public void RequestCancellation() { }
        void System.Threading.IThreadPoolWorkItem.Execute() { }
        protected System.Runtime.InteropServices.SafeHandle TakeAcceptedHandle() { throw null; }
    }
    public enum IoUringOperationKind
    {
        Read = 0,
        Write = 1,
        ReadScatter = 2,
        WriteGather = 3,
        Accept = 4,
        Connect = 5,
        Receive = 6,
        Send = 7,
        SendGather = 10,
        PollRead = 11,
        PollWrite = 12,
        PollMultishot = 13,
        AcceptMultishot = 14,
        SendZeroCopy = 15,
    }
    [System.FlagsAttribute]
    public enum IoUringPollEvents
    {
        None = 0,
        Readable = 1,
        Priority = 2,
        Writable = 4,
        Error = 8,
        Hangup = 16,
        Invalid = 32,
        ReadHangup = 8192,
    }
    [System.CLSCompliantAttribute(false)]
    public abstract partial class IoUringPollOperation : System.Threading.IoUringMultishotOperation<System.Threading.IoUringPollEvents>
    {
        protected IoUringPollOperation(System.Threading.IoUringPollEvents events) { }
        protected sealed override System.Threading.IoUringRequest PrepareRequest() { throw null; }
        protected sealed override System.Threading.IoUringCompletionAction ProcessCompletion(in System.Threading.IoUringCompletion completion) { throw null; }
    }
    [System.CLSCompliantAttribute(false)]
    public abstract partial class IoUringReceiveOperation : System.Threading.IoUringMultishotOperation<System.Buffers.IMemoryOwner<byte>>
    {
        protected IoUringReceiveOperation() { }
        protected virtual System.Exception CreateException(int errorCode) { throw null; }
        protected sealed override System.Threading.IoUringRequest PrepareRequest() { throw null; }
        protected sealed override System.Threading.IoUringCompletionAction ProcessCompletion(in System.Threading.IoUringCompletion completion) { throw null; }
    }
    [System.CLSCompliantAttribute(false)]
    public readonly partial struct IoUringRequest
    {
        private readonly object _dummy;
        private readonly int _dummyPrimitive;
        public IoUringRequest(System.Threading.IoUringOperationKind kind, System.Memory<byte> buffer, long offset = (long)-1, int flags = 0) { throw null; }
        public IoUringRequest(System.Threading.IoUringOperationKind kind, System.ReadOnlyMemory<byte> buffer, long offset = (long)-1, int flags = 0) { throw null; }
        public unsafe IoUringRequest(System.Threading.IoUringOperationKind kind, void* address, int length, long offset = (long)-1, int flags = 0, int* addressLength = null) { throw null; }
        public static System.Threading.IoUringRequest CreateUnsafe(in System.Threading.IoUringSubmission submission) { throw null; }
        public System.Threading.IoUringRequest SliceBuffer(int offset, int length) { throw null; }
        public System.Threading.IoUringRequest WithOffset(long offset) { throw null; }
    }
    [System.CLSCompliantAttribute(false)]
    public readonly partial struct IoUringSubmission
    {
        private readonly int _dummyPrimitive;
        public unsafe IoUringSubmission(byte opcode, void* address, int length, ulong offset = (ulong)0, uint operationFlags = (uint)0, ushort priority = (ushort)0, System.Threading.IoUringSubmissionOptions options = System.Threading.IoUringSubmissionOptions.None, ulong address3 = (ulong)0) { throw null; }
        public nint Address { get { throw null; } }
        public ulong Address3 { get { throw null; } }
        public int Length { get { throw null; } }
        public ulong Offset { get { throw null; } }
        public byte Opcode { get { throw null; } }
        public uint OperationFlags { get { throw null; } }
        public System.Threading.IoUringSubmissionOptions Options { get { throw null; } }
        public ushort Priority { get { throw null; } }
    }
    [System.FlagsAttribute]
    public enum IoUringSubmissionOptions
    {
        None = 0,
        ForceAsync = 16,
    }
    public partial interface IThreadPoolWorkItem
    {
        void Execute();
    }
#if !FEATURE_WASM_MANAGED_THREADS
    [System.Runtime.Versioning.UnsupportedOSPlatformAttribute("browser")]
#endif
    public sealed partial class RegisteredWaitHandle : System.MarshalByRefObject
    {
        internal RegisteredWaitHandle() { }
        public bool Unregister(System.Threading.WaitHandle? waitObject) { throw null; }
    }
    public static partial class ThreadPool
    {
        public static long CompletedWorkItemCount { get { throw null; } }
        public static long PendingWorkItemCount { get { throw null; } }
        public static int ThreadCount { get { throw null; } }
        [System.ObsoleteAttribute("ThreadPool.BindHandle(IntPtr) has been deprecated. Use ThreadPool.BindHandle(SafeHandle) instead.")]
        [System.Runtime.Versioning.SupportedOSPlatformAttribute("windows")]
        public static bool BindHandle(System.IntPtr osHandle) { throw null; }
        [System.Runtime.Versioning.SupportedOSPlatformAttribute("windows")]
        public static bool BindHandle(System.Runtime.InteropServices.SafeHandle osHandle) { throw null; }
        public static void GetAvailableThreads(out int workerThreads, out int completionPortThreads) { throw null; }
        public static void GetMaxThreads(out int workerThreads, out int completionPortThreads) { throw null; }
        public static void GetMinThreads(out int workerThreads, out int completionPortThreads) { throw null; }
        public static bool QueueUserWorkItem(System.Threading.WaitCallback callBack) { throw null; }
        public static bool QueueUserWorkItem(System.Threading.WaitCallback callBack, object? state) { throw null; }
        public static bool QueueUserWorkItem<TState>(System.Action<TState> callBack, TState state, bool preferLocal) { throw null; }
#if !FEATURE_WASM_MANAGED_THREADS
        [System.Runtime.Versioning.UnsupportedOSPlatformAttribute("browser")]
#endif
        public static System.Threading.RegisteredWaitHandle RegisterWaitForSingleObject(System.Threading.WaitHandle waitObject, System.Threading.WaitOrTimerCallback callBack, object? state, int millisecondsTimeOutInterval, bool executeOnlyOnce) { throw null; }
#if !FEATURE_WASM_MANAGED_THREADS
        [System.Runtime.Versioning.UnsupportedOSPlatformAttribute("browser")]
#endif
        public static System.Threading.RegisteredWaitHandle RegisterWaitForSingleObject(System.Threading.WaitHandle waitObject, System.Threading.WaitOrTimerCallback callBack, object? state, long millisecondsTimeOutInterval, bool executeOnlyOnce) { throw null; }
#if !FEATURE_WASM_MANAGED_THREADS
        [System.Runtime.Versioning.UnsupportedOSPlatformAttribute("browser")]
#endif
        public static System.Threading.RegisteredWaitHandle RegisterWaitForSingleObject(System.Threading.WaitHandle waitObject, System.Threading.WaitOrTimerCallback callBack, object? state, System.TimeSpan timeout, bool executeOnlyOnce) { throw null; }
        [System.CLSCompliantAttribute(false)]
#if !FEATURE_WASM_MANAGED_THREADS
        [System.Runtime.Versioning.UnsupportedOSPlatformAttribute("browser")]
#endif
        public static System.Threading.RegisteredWaitHandle RegisterWaitForSingleObject(System.Threading.WaitHandle waitObject, System.Threading.WaitOrTimerCallback callBack, object? state, uint millisecondsTimeOutInterval, bool executeOnlyOnce) { throw null; }
        public static bool SetMaxThreads(int workerThreads, int completionPortThreads) { throw null; }
        public static bool SetMinThreads(int workerThreads, int completionPortThreads) { throw null; }
        [System.CLSCompliantAttribute(false)]
        [System.Runtime.Versioning.SupportedOSPlatformAttribute("windows")]
        public unsafe static bool UnsafeQueueNativeOverlapped(System.Threading.NativeOverlapped* overlapped) { throw null; }
        public static bool UnsafeQueueUserWorkItem(System.Threading.IThreadPoolWorkItem callBack, bool preferLocal) { throw null; }
        public static bool UnsafeQueueUserWorkItem(System.Threading.WaitCallback callBack, object? state) { throw null; }
        public static bool UnsafeQueueUserWorkItem<TState>(System.Action<TState> callBack, TState state, bool preferLocal) { throw null; }
#if !FEATURE_WASM_MANAGED_THREADS
        [System.Runtime.Versioning.UnsupportedOSPlatformAttribute("browser")]
#endif
        public static System.Threading.RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(System.Threading.WaitHandle waitObject, System.Threading.WaitOrTimerCallback callBack, object? state, int millisecondsTimeOutInterval, bool executeOnlyOnce) { throw null; }
#if !FEATURE_WASM_MANAGED_THREADS
        [System.Runtime.Versioning.UnsupportedOSPlatformAttribute("browser")]
#endif
        public static System.Threading.RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(System.Threading.WaitHandle waitObject, System.Threading.WaitOrTimerCallback callBack, object? state, long millisecondsTimeOutInterval, bool executeOnlyOnce) { throw null; }
#if !FEATURE_WASM_MANAGED_THREADS
        [System.Runtime.Versioning.UnsupportedOSPlatformAttribute("browser")]
#endif
        public static System.Threading.RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(System.Threading.WaitHandle waitObject, System.Threading.WaitOrTimerCallback callBack, object? state, System.TimeSpan timeout, bool executeOnlyOnce) { throw null; }
        [System.CLSCompliantAttribute(false)]
#if !FEATURE_WASM_MANAGED_THREADS
        [System.Runtime.Versioning.UnsupportedOSPlatformAttribute("browser")]
#endif
        public static System.Threading.RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(System.Threading.WaitHandle waitObject, System.Threading.WaitOrTimerCallback callBack, object? state, uint millisecondsTimeOutInterval, bool executeOnlyOnce) { throw null; }
    }
    public delegate void WaitCallback(object? state);
    public delegate void WaitOrTimerCallback(object? state, bool timedOut);
}
