// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;

internal static partial class Interop
{
    internal static partial class Sys
    {
        // Returns the consumed prefix length, including empty vectors. The remaining span
        // starts at the first unwritten byte, even after repeated partial completions.
        internal static unsafe int AdvanceIOVectors(Span<IOVector> vectors, long bytesTransferred)
        {
            Debug.Assert(bytesTransferred >= 0);
            int index = 0;
            while (index < vectors.Length)
            {
                ref IOVector vector = ref vectors[index];
                if ((ulong)bytesTransferred < (ulong)vector.Count)
                {
                    vector.Base += (nint)bytesTransferred;
                    vector.Count -= (nuint)bytesTransferred;
                    break;
                }

                bytesTransferred -= (long)vector.Count;
                index++;
            }

            Debug.Assert(index < vectors.Length || bytesTransferred == 0);
            return index;
        }
    }
}
