// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;

namespace System.Threading
{
    internal sealed partial class PortableThreadPool
    {
        internal static partial class IoUringThreadPool
        {
            private static readonly bool s_isAdaptive;
            private static readonly Lock s_adjustmentLock = new();
            private static readonly ConcurrentDictionary<MultishotReceiveOperation, byte> s_activeReceives = new();
            private static Ring[]? s_receiveRings;
            private static Ring[]? s_allRings;
            private static bool s_scalingFailed;

            private static Ring GetReceiveRing(IntPtr fd)
            {
                Ring[] rings = Volatile.Read(ref s_receiveRings)!;
                return rings[(uint)(nuint)(nint)fd % (uint)rings.Length];
            }

            private static Ring? CreateRing(int index, int bufferSize, int bufferCount, out int error)
            {
                using ManualResetEventSlim ready = new(false);
                Ring ring = new(index);
                bool created = false;
                int creationError = 0;
                Thread issuer = new(() =>
                {
                    // The creator must own the ring for its lifetime. Until readiness is signaled,
                    // use only captured locals to avoid waiting on the outer type's initializer.
                    created = Interop.Sys.IoRingCreate(QueueDepth, QueueDepth, singleIssuer: 1, out ring.RingHandle) == 0;
                    if (created)
                    {
                        ring.WakeEventFd = Interop.Sys.IoRingRegisterEventFd(ring.RingHandle);
                        created = ring.WakeEventFd >= 0;
                    }
                    if (created)
                    {
                        unsafe
                        {
                            byte* storage = null;
                            created = Interop.Sys.IoRingRegisterBufferRing(ring.RingHandle, bufferSize, bufferCount, &storage) == 0;
                            if (created)
                            {
                                ring.ReceiveBuffers = new ReceiveBufferPool(ring, bufferSize, bufferCount, storage);
                            }
                        }
                    }
                    if (!created)
                    {
                        creationError = Marshal.GetLastPInvokeError();
                        if (ring.RingHandle != IntPtr.Zero)
                        {
                            Interop.Sys.IoRingClose(ring.RingHandle);
                        }
                    }
                    ready.Set();
                    if (created)
                    {
                        IssuerLoop(ring);
                    }
                })
                {
                    IsBackground = true,
                    Name = $".NET IoUring Issuer #{index}",
                };
                issuer.UnsafeStart();
                ready.Wait();
                error = creationError;
                return created ? ring : null;
            }

            private static void SampleIssuerCpu(Ring ring)
            {
                long cpuTime = Interop.Sys.IoRingGetThreadCpuTime();
                long timestamp = Stopwatch.GetTimestamp();
                if (cpuTime < 0)
                {
                    ScalingEventSource.Log.AdjustmentFailed(Marshal.GetLastPInvokeError());
                    Volatile.Write(ref s_scalingFailed, true);
                }
                else
                {
                    if (ring.LastSampleTimestamp != 0)
                    {
                        double elapsedNanoseconds = (timestamp - ring.LastSampleTimestamp) * (1_000_000_000.0 / Stopwatch.Frequency);
                        Volatile.Write(ref ring.CpuUtilization,
                            (int)Math.Clamp((cpuTime - ring.LastCpuTime) * 100.0 / elapsedNanoseconds, 0, 100));
                    }
                    ring.LastCpuTime = cpuTime;
                    ring.LastSampleTimestamp = timestamp;
                }
                Volatile.Write(ref ring.SampleRequested, 0);
            }

            private static void AdjustIssuerCount()
            {
                int minimum = s_rings!.Length;
                int maximum = Math.Max(minimum, Environment.ProcessorCount / 2);
                IssuerCountPolicy policy = new(minimum, maximum);
                while (!Volatile.Read(ref s_scalingFailed))
                {
                    Thread.Sleep(1000);
                    if (Volatile.Read(ref s_scalingFailed))
                    {
                        return;
                    }
                    Ring[] rings = Volatile.Read(ref s_receiveRings)!;
                    int utilization = 0;
                    bool samplesReady = true;
                    foreach (Ring ring in rings)
                    {
                        samplesReady &= Volatile.Read(ref ring.SampleRequested) == 0;
                        utilization += Volatile.Read(ref ring.CpuUtilization);
                    }
                    utilization /= rings.Length;
                    ScalingEventSource.Log.Sampled(rings.Length, utilization);
                    int count = policy.GetNextCount(rings.Length, utilization, samplesReady, !s_activeReceives.IsEmpty);
                    if (count != rings.Length)
                    {
                        if (!ResizeReceiveRings(count))
                        {
                            return;
                        }
                        ScalingEventSource.Log.Resized(rings.Length, count, utilization);
                    }

                    foreach (Ring ring in Volatile.Read(ref s_receiveRings)!)
                    {
                        Volatile.Write(ref ring.SampleRequested, 1);
                        if (Interop.Sys.EventFdWrite(ring.WakeEventFd) < 0)
                        {
                            ScalingEventSource.Log.AdjustmentFailed(Marshal.GetLastPInvokeError());
                            Volatile.Write(ref s_scalingFailed, true);
                            return;
                        }
                    }
                }
            }

            private sealed class IssuerCountPolicy
            {
                private const int BusyUtilization = 95;
                private const int IdleUtilization = 40;
                private const int BusySamplesBeforeGrowth = 3;
                private const int IdleSamplesBeforeReduction = 10;
                private const int CooldownSamples = 5;
                private readonly int _minimum;
                private readonly int _maximum;
                private int _busySamples;
                private int _idleSamples;
                private int _cooldown;

                public IssuerCountPolicy(int minimum, int maximum)
                {
                    _minimum = minimum;
                    _maximum = maximum;
                }

                public int GetNextCount(int count, int utilization, bool samplesReady, bool hasReceives)
                {
                    if (_cooldown > 0)
                    {
                        _cooldown--;
                        return count;
                    }
                    if (!samplesReady)
                    {
                        _busySamples = _idleSamples = 0;
                        return count;
                    }

                    _busySamples = hasReceives && utilization >= BusyUtilization ? _busySamples + 1 : 0;
                    _idleSamples = utilization < IdleUtilization ? _idleSamples + 1 : 0;
                    int next = count;
                    if (_busySamples >= BusySamplesBeforeGrowth && count < _maximum)
                    {
                        next = Math.Min(_maximum, count * 2);
                    }
                    else if (!hasReceives)
                    {
                        next = _minimum;
                    }
                    else if (_idleSamples >= IdleSamplesBeforeReduction && count > _minimum)
                    {
                        next = Math.Max(_minimum, count / 2);
                    }
                    if (next != count)
                    {
                        _busySamples = _idleSamples = 0;
                        _cooldown = CooldownSamples;
                    }
                    return next;
                }
            }

            private static bool ResizeReceiveRings(int count)
            {
                Debug.Assert(s_isAdaptive && count > 0);
                lock (s_adjustmentLock)
                {
                    Ring[] allRings = s_allRings!;
                    if (count > allRings.Length)
                    {
                        int existing = allRings.Length;
                        Array.Resize(ref allRings, count);
                        for (int i = existing; i < count; i++)
                        {
                            Ring? ring = CreateRing(i, GetReceiveBufferSize(), GetReceiveBufferCount(), out int error);
                            if (ring is null)
                            {
                                Array.Resize(ref allRings, i);
                                s_allRings = allRings;
                                ScalingEventSource.Log.AdjustmentFailed(error);
                                Volatile.Write(ref s_scalingFailed, true);
                                return false;
                            }
                            allRings[i] = ring;
                        }
                        s_allRings = allRings;
                    }

                    // Inactive rings remain alive: callers can retain their selected buffers after
                    // migrating. Their original issuer parks once the old submissions drain.
                    Ring[] active = allRings.AsSpan(0, count).ToArray();
                    Volatile.Write(ref s_receiveRings, active);
                    foreach (MultishotReceiveOperation operation in s_activeReceives.Keys)
                    {
                        operation.RequestMigration();
                    }
                    return true;
                }
            }

            [EventSource(Name = "System.Threading.IoUring")]
            private sealed class ScalingEventSource : EventSource
            {
                internal static readonly ScalingEventSource Log = new();

                [Event(1, Level = EventLevel.Informational)]
                public void Resized(int previousCount, int currentCount, int utilization) =>
                    WriteEvent(1, previousCount, currentCount, utilization);

                [Event(2, Level = EventLevel.Warning)]
                public void AdjustmentFailed(int error) => WriteEvent(2, error);

                [Event(3, Level = EventLevel.Verbose)]
                public void Sampled(int count, int utilization) => WriteEvent(3, count, utilization);
            }
        }
    }
}
