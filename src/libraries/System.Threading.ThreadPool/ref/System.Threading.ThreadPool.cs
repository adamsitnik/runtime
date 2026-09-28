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
    // src/libraries/System.Private.CoreLib/src/System/Threading/PortableThreadPool.IoUring.Unix.cs for details.
    [System.CLSCompliantAttribute(false)]
    public interface IIoUringOperation : System.Threading.IThreadPoolWorkItem
    {
        System.Threading.IoUringRequest Request { get; }
        System.Threading.IoUringOperationStatus IssuerThread(int result, uint flags, long sequence);
        void RequestCancellation();
    }
    [System.CLSCompliantAttribute(false)]
    public abstract class IoUringOperation : System.Threading.IIoUringOperation
    {
        protected IoUringOperation() { }
        public bool IsCancellationRequested { get { throw null; } }
        public bool IsPending { get { throw null; } }
        public abstract System.Threading.IoUringRequest Request { get; }
        public abstract void Execute();
        public abstract System.Threading.IoUringOperationStatus IssuerThread(int result, uint flags, long sequence);
        public void RequestCancellation() { }
    }
    public enum IoUringOperationStatus
    {
        Done = 0,
        ReSubmit = 1,
        Schedule = 2,
    }
    public readonly partial struct IoUringRequest
    {
        public static System.Threading.IoUringRequest Receive(System.IntPtr buffer, int length, int flags = 0) { throw null; }
        public static System.Threading.IoUringRequest Send(System.IntPtr buffer, int length, int flags = 0) { throw null; }
    }
    // EXPERIMENTAL, PROTOTYPE-ONLY: see the real implementation in
    // src/libraries/System.Private.CoreLib/src/System/Threading/IoUring.Unix.cs for details.
    [System.CLSCompliantAttribute(false)]
    public static class IoUring
    {
        public static bool IsSupported { get { throw null; } }
        public static bool TrySubmit(System.Runtime.InteropServices.SafeHandle handle, System.Threading.IIoUringOperation operation) { throw null; }
        public static bool TrySubmit(System.Runtime.InteropServices.SafeHandle handle, System.Threading.IIoUringOperation first, System.Threading.IIoUringOperation second) { throw null; }
        public static unsafe bool TrySubmitRecv(System.Runtime.InteropServices.SafeHandle handle, byte* buffer, int length, int flags, System.Action<int> onCompleted) { throw null; }
        public static unsafe bool TrySubmitSend(System.Runtime.InteropServices.SafeHandle handle, byte* buffer, int length, int flags, System.Action<int> onCompleted) { throw null; }
        public static unsafe bool TrySubmitAccept(System.Runtime.InteropServices.SafeHandle handle, byte* sockAddr, int* sockAddrLen, int flags, System.Action<int> onCompleted) { throw null; }
        public static unsafe bool TrySubmitConnect(System.Runtime.InteropServices.SafeHandle handle, byte* sockAddr, int* sockAddrLen, System.Action<int> onCompleted) { throw null; }
        public static bool TrySubmitRecvMultishot(System.Runtime.InteropServices.SafeHandle handle, System.Action<int, System.Buffers.IMemoryOwner<byte>?, bool> onCompleted, out System.Threading.IIoUringOperation? operation) { throw null; }
    }
}
