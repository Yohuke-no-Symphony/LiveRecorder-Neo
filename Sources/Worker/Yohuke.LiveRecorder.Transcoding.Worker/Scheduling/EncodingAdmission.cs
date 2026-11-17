using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Resources;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Scheduling;

internal sealed class EncodingAdmission
{
    private readonly Lock sync = new();
    private int runningGpu;
    private int runningCpu;
    private int runningCpuThreads;
    private bool probeActive;

    internal (int Gpu, int Cpu) Running
    {
        get
        {
            lock (sync)
            {
                return (runningGpu, runningCpu);
            }
        }
    }

    internal EncodingReservation? TryAcquire(WorkerConfiguration configuration, WorkerCapabilities capabilities,
        CpuLoadSnapshot cpu, TimeSpan gpuWaiting)
    {
        lock (sync)
        {
            if (probeActive || runningCpu + runningGpu >= configuration.MaxConcurrentEncodes)
            {
                return null;
            }
            var gpu = capabilities.Encoders.FirstOrDefault(encoder => encoder.Available && encoder.Encoder != "libx264"
                && (configuration.PreferredEncoder == "auto" || configuration.PreferredEncoder == encoder.Encoder)
                && (configuration.DeviceId is null || configuration.DeviceId == encoder.DeviceId));
            if (gpu is not null && runningGpu < configuration.GpuConcurrency)
            {
                runningGpu++;
                return new(gpu with { }, 0, () => Release(false, 0));
            }
            var budget = ThreadBudget(configuration, cpu);
            var software = capabilities.Encoders.FirstOrDefault(encoder => encoder.Available && encoder.Encoder == "libx264");
            if (configuration.CpuFallbackMode != "Balanced" || runningCpu >= configuration.CpuConcurrency || software is null
                || gpuWaiting <= TimeSpan.FromSeconds(configuration.GpuWaitSeconds) || cpu.AveragePercent is not { } usage
                || usage >= configuration.CpuLoadThreshold || budget <= runningCpuThreads)
            {
                return null;
            }
            var threads = Math.Min(budget - runningCpuThreads, Math.Max(1, budget / Math.Max(1, configuration.CpuConcurrency)));
            runningCpu++;
            runningCpuThreads += threads;
            return new(software with { }, threads, () => Release(true, threads));
        }
    }

    internal EncodingReservation? TryAcquireProbe(WorkerConfiguration configuration)
    {
        lock (sync)
        {
            if (runningGpu > 0 || runningCpu + runningGpu >= configuration.MaxConcurrentEncodes)
            {
                return null;
            }
            runningGpu++;
            probeActive = true;
            return new(new() { Encoder = "capability-probe" }, 1, () =>
            {
                lock (sync)
                {
                    probeActive = false;
                    runningGpu--;
                }
            });
        }
    }

    internal static int ThreadBudget(WorkerConfiguration configuration, CpuLoadSnapshot cpu) =>
        double.IsFinite(cpu.EffectiveProcessors) && cpu.EffectiveProcessors > 0
            ? Math.Max(0, (int)Math.Floor(cpu.EffectiveProcessors * configuration.CpuThreadFraction)) : 0;

    internal static bool HasPossibleCapacity(WorkerConfiguration configuration, WorkerCapabilities capabilities, CpuLoadSnapshot cpu)
    {
        var gpu = configuration.GpuConcurrency > 0 && capabilities.Encoders.Any(encoder => encoder.Available && encoder.Encoder != "libx264"
            && (configuration.PreferredEncoder == "auto" || configuration.PreferredEncoder == encoder.Encoder)
            && (configuration.DeviceId is null || configuration.DeviceId == encoder.DeviceId));
        var software = configuration.CpuFallbackMode == "Balanced" && configuration.CpuConcurrency > 0 && ThreadBudget(configuration, cpu) > 0
            && capabilities.Encoders.Any(encoder => encoder.Available && encoder.Encoder == "libx264");
        return gpu || software;
    }

    private void Release(bool cpu, int threads)
    {
        lock (sync)
        {
            if (cpu)
            {
                runningCpu--;
                runningCpuThreads -= threads;
            }
            else
            {
                runningGpu--;
            }
        }
    }
}
