// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading;

namespace System.Net.Sockets;

public sealed partial class SafeSocketHandle
{
    private IoRingBoundHandle? _ioUringBinding;
    private bool _ioUringDisposed;

    internal bool IsIoUringDisposed => Volatile.Read(ref _ioUringDisposed);

    internal IoRingBoundHandle IoUringBinding
    {
        get
        {
            IoRingBoundHandle? binding = Volatile.Read(ref _ioUringBinding);
            if (binding is null)
            {
                // Prevent first-time binding after disposal, even if another reference keeps IsClosed false.
                ObjectDisposedException.ThrowIf(IsIoUringDisposed, this);
                binding = IoUring.Bind(this, ownsFileDescriptor: OwnsHandle);
                binding = Interlocked.CompareExchange(ref _ioUringBinding, binding, null) ?? binding;

                // Disposal may have observed no binding before this publication.
                if (IsIoUringDisposed)
                {
                    binding.DisposeAndWait();
                    throw new ObjectDisposedException(nameof(SafeSocketHandle));
                }
            }

            return binding;
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        // Fence the closing flag before checking for a concurrently published binding.
        Interlocked.Exchange(ref _ioUringDisposed, true);
        Volatile.Read(ref _ioUringBinding)?.Dispose();
        base.Dispose(disposing);
    }
}
