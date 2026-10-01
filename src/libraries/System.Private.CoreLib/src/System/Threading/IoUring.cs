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
        /// <returns>The shared binding for this handle.</returns>
        /// <remarks>
        /// The handle owner must retain the binding, submit operations through
        /// <see cref="IoRingBoundHandle.EnqueueForSubmission"/>, and dispose the binding before disposing the handle.
        /// Disposing the handle alone does not cancel operations or release the binding's handle reference.
        /// Binding disposal requests cancellation but does not end the lifetime of outstanding operation buffers.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="handle"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="handle"/> is invalid.</exception>
        /// <exception cref="PlatformNotSupportedException">io_uring is unavailable.</exception>
        /// <exception cref="ObjectDisposedException">The handle or its binding has been disposed.</exception>
        public static IoRingBoundHandle Bind(SafeHandle handle)
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
                return IoRingBoundHandle.GetOrCreate(handle);
            }
#endif
            throw new PlatformNotSupportedException();
        }
    }
}
