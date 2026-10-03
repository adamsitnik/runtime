// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading;

/// <summary>Represents an io_uring operation that produces an ordered sequence of typed results.</summary>
/// <typeparam name="T">The type of each produced result.</typeparam>
[CLSCompliant(false)]
public abstract class IoUringMultishotOperation<T> : IoUringOperation
{
    /// <summary>Initializes a new instance of the <see cref="IoUringMultishotOperation{T}"/> class.</summary>
    protected IoUringMultishotOperation()
    {
    }

    /// <summary>Delivers one result to the consumer.</summary>
    /// <param name="result">The produced result.</param>
    /// <remarks>
    /// Calls are ordered and nonconcurrent. Ownership of disposable results transfers at callback entry;
    /// the consumer must dispose them even if it throws. Logical completion is reported separately.
    /// </remarks>
    protected abstract void OnNext(T result);
}
