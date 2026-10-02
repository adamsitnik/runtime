// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace System.Threading
{
    /// <summary>
    /// Exposes the experimental io_uring Thread Pool infrastructure to other framework components.
    /// </summary>
    /// <remarks>
    /// This prototype uses sharded rings on Linux.
    /// Each ring has one dedicated issuer; completion callbacks run on Thread Pool workers.
    /// The API may change incompatibly or be removed without notice.
    /// </remarks>
    [CLSCompliant(false)]
    public static class IoUring
    {
        /// <summary>Gets a value that indicates whether io_uring is enabled and usable on this system.</summary>
        public static bool IsSupported =>
#if FEATURE_IO_URING
            PortableThreadPool.IoUringThreadPool.IsEnabled;
#else
            false;
#endif

        /// <summary>Gets the canonical binding of a handle to one io_uring issuer.</summary>
        /// <param name="handle">The handle to bind without transferring its ownership.</param>
        /// <param name="ownsFileDescriptor">
        /// <see langword="true"/> if <paramref name="handle"/> owns and closes the underlying file descriptor;
        /// otherwise, <see langword="false"/> for a borrowed wrapper.
        /// </param>
        /// <returns>The shared binding for this file descriptor.</returns>
        /// <remarks>
        /// The handle owner must retain the binding, submit operations through
        /// <see cref="IoRingBoundHandle.EnqueueForSubmission"/>, and dispose the binding before disposing the handle.
        /// Disposing the handle alone does not cancel operations or release the binding's handle reference.
        /// Binding disposal requests cancellation but does not end the lifetime of outstanding operation buffers.
        /// Different handle wrappers for the same file descriptor share admission and cancellation.
        /// The binding retains one handle until native requests drain. If a borrowed handle binds first,
        /// an owning handle replaces it when that owner binds. Other borrowed wrappers are not retained.
        /// Until the owner binds, the caller must keep the underlying descriptor open.
        /// A different owning wrapper for an already-owned descriptor is rejected.
        /// The caller must accurately describe descriptor ownership; this argument does not change it.
        /// A handle that runs cleanup on release without closing the descriptor is a borrowed wrapper.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="handle"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="handle"/> is invalid, or a different owning handle is already bound to its descriptor.
        /// </exception>
        /// <exception cref="PlatformNotSupportedException">io_uring is unavailable.</exception>
        /// <exception cref="ObjectDisposedException">The handle or its binding has been disposed.</exception>
        public static IoRingBoundHandle Bind(SafeHandle handle, bool ownsFileDescriptor)
        {
            ArgumentNullException.ThrowIfNull(handle);
            ObjectDisposedException.ThrowIf(handle.IsClosed, handle);
            if (handle.IsInvalid)
            {
                // SafeSocketHandle.IsInvalid also observes IsClosed, which can change
                // after the preceding check.
                ObjectDisposedException.ThrowIf(handle.IsClosed, handle);
                throw new ArgumentException(SR.Arg_InvalidHandle, nameof(handle));
            }
#if FEATURE_IO_URING
            if (IsSupported)
            {
                return IoRingBoundHandle.GetOrCreate(handle, ownsFileDescriptor);
            }
#endif
            throw new PlatformNotSupportedException();
        }
    }
}
