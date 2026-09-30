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
            private const int BootstrapSampleIntervalMs = 250;
            private const int SampleIntervalMs = 1000;
            private static readonly bool s_isAdaptive;
            private static readonly Lock s_adjustmentLock = new();
            private static readonly ConcurrentDictionary<MultishotReceiveOperation, byte> s_activeReceives = new();
            private static Ring[]? s_allRings;
            private static bool s_scalingFailed;

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
                const int Minimum = 1;
                const int CoresPerBootstrapIssuer = 8;
                int maximum = Math.Max(Minimum, Environment.ProcessorCount / 2);
                int bootstrapCount = (int)System.Numerics.BitOperations.RoundUpToPowerOf2(
                    (uint)Math.Max(Minimum, Environment.ProcessorCount / CoresPerBootstrapIssuer));
                IssuerCountPolicy policy = new(Minimum, maximum, bootstrapCount);
                while (!Volatile.Read(ref s_scalingFailed))
                {
                    Thread.Sleep(Volatile.Read(ref s_rings)!.Length < bootstrapCount ? BootstrapSampleIntervalMs : SampleIntervalMs);
                    if (Volatile.Read(ref s_scalingFailed))
                    {
                        return;
                    }
                    Ring[] rings = Volatile.Read(ref s_rings)!;
                    int utilization = 0;
                    bool samplesReady = true;
                    foreach (Ring ring in rings)
                    {
                        samplesReady &= Volatile.Read(ref ring.SampleRequested) == 0;
                        utilization += Volatile.Read(ref ring.CpuUtilization);
                    }
                    utilization /= rings.Length;
                    ScalingEventSource.Log.Sampled(rings.Length, utilization);
                    int count = policy.GetNextCount(rings.Length, utilization, samplesReady);
                    if (count != rings.Length)
                    {
                        if (!ResizeRings(count))
                        {
                            return;
                        }
                        ScalingEventSource.Log.Resized(rings.Length, count, utilization);
                    }

                    foreach (Ring ring in Volatile.Read(ref s_rings)!)
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
                private const int BootstrapBusyUtilization = 85;
                private const int IdleUtilization = 40;
                private const int BusySamplesBeforeGrowth = 3;
                private const int IdleSamplesBeforeReduction = 10;
                private const int CooldownSamples = 5;
                private readonly int _minimum;
                private readonly int _maximum;
                private readonly int _bootstrapCount;
                private int _busySamples;
                private int _idleSamples;
                private int _cooldown;

                public IssuerCountPolicy(int minimum, int maximum, int bootstrapCount)
                {
                    _minimum = minimum;
                    _maximum = maximum;
                    _bootstrapCount = bootstrapCount;
                }

                public int GetNextCount(int count, int utilization, bool samplesReady)
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

                    // Approach the prior CPU-based startup count with less pressure, but require
                    // near-total saturation beyond it to avoid crowding out application workers.
                    int busyUtilization = count < _bootstrapCount ? BootstrapBusyUtilization : BusyUtilization;
                    _busySamples = utilization >= busyUtilization ? _busySamples + 1 : 0;
                    _idleSamples = utilization < IdleUtilization ? _idleSamples + 1 : 0;
                    int next = count;
                    if (_busySamples >= BusySamplesBeforeGrowth && count < _maximum)
                    {
                        next = Math.Min(count < _bootstrapCount ? _bootstrapCount : _maximum, count * 2);
                    }
                    else if (_idleSamples >= IdleSamplesBeforeReduction && count > _minimum)
                    {
                        next = Math.Max(_minimum, count / 2);
                    }
                    if (next != count)
                    {
                        _busySamples = _idleSamples = 0;
                        // Sustained saturation already establishes the need for another issuer.
                        // Delay only after a reduction to avoid immediately reversing it.
                        _cooldown = next < count ? CooldownSamples : 0;
                    }
                    return next;
                }
            }

            private static bool ResizeRings(int count)
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
                    Volatile.Write(ref s_rings, active);
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
