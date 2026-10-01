// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// ------------------------------------------------------------------------------
// Changes to this file must follow the https://aka.ms/api-review process.
// ------------------------------------------------------------------------------

namespace System.Threading
{
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
    // EXPERIMENTAL, PROTOTYPE-ONLY: see the real implementation in
    // src/libraries/System.Private.CoreLib/src/System/Threading/IoUring.Linux.cs for details.
    [System.CLSCompliantAttribute(false)]
    public static class IoUring
    {
        public static bool IsSupported { get { throw null; } }
        public static System.Threading.IoRingBoundHandle Bind(System.Runtime.InteropServices.SafeHandle handle) { throw null; }
    }
    [System.CLSCompliantAttribute(false)]
    public sealed class IoRingBoundHandle : System.IDisposable, System.Threading.IThreadPoolWorkItem
    {
        internal IoRingBoundHandle() { }
        public void Dispose() { }
        public bool DisposeAndWait() { throw null; }
        public void Enqueue(System.Threading.IoUringOperation operation, System.Threading.CancellationToken cancellationToken = default) { }
        void System.Threading.IThreadPoolWorkItem.Execute() { }
    }
    [System.CLSCompliantAttribute(false)]
    public abstract class IoUringOperation : System.Threading.IThreadPoolWorkItem
    {
        protected IoUringOperation() { }
        protected bool IsCancellationRequested { get { throw null; } }
        protected abstract System.Threading.IoUringRequest Request { get; }
        protected void CompleteOperation() { }
        public static System.Threading.IoUringOperation CreateReceiveMultishot(System.Action<int, System.Buffers.IMemoryOwner<byte>?, bool> onCompleted) { throw null; }
        protected void EnqueueContinuation(System.Threading.IoUringRequest request) { }
        protected abstract void OnCompleted(int result, uint flags, long sequence);
        public void RequestCancellation() { }
        void System.Threading.IThreadPoolWorkItem.Execute() { }
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
    }
    [System.CLSCompliantAttribute(false)]
    public readonly unsafe struct IoUringRequest
    {
        public IoUringRequest(System.Threading.IoUringOperationKind kind, void* address, int length, long offset = -1, int flags = 0, int* addressLength = null) { }
    }
}
