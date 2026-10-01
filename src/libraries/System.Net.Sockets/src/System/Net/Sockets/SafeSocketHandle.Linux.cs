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
                lock (this)
                {
                    // Dispose uses the same lock so it cannot miss a first-time binding.
                    ObjectDisposedException.ThrowIf(_ioUringDisposed, this);
                    binding = _ioUringBinding;
                    if (binding is null)
                    {
                        binding = IoUring.Bind(this);
                        Volatile.Write(ref _ioUringBinding, binding);
                    }
                }
            }

            return binding;
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        IoRingBoundHandle? binding;
        lock (this)
        {
            Volatile.Write(ref _ioUringDisposed, true);
            binding = _ioUringBinding;
        }
        binding?.Dispose();
        base.Dispose(disposing);
    }
}
