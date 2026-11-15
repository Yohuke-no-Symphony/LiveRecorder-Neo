using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Encoding;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

internal sealed class WorkerConfigurationManager
{
    private readonly Lock sync = new();
    private readonly Func<WorkerConfiguration, IDisposable?> reserveProbe;
    private readonly Func<WorkerConfiguration, CancellationToken, Task<WorkerCapabilities>> probe;
    private readonly TimeProvider timeProvider;
    private WorkerRuntimeConfiguration current = new(new(), new(), 0);
    private int requestedRevision;
    private string? error = "Loading server configuration";
    private CancellationTokenSource? refreshCancellation;
    private Task refreshTask = Task.CompletedTask;
    private long lastProbe;
    private bool hasProbed;

    internal WorkerConfigurationManager(WorkerProcessRunner processes, Func<WorkerConfiguration, IDisposable?> reserveProbe,
        Func<WorkerConfiguration, CancellationToken, Task<WorkerCapabilities>>? probe = null, TimeProvider? timeProvider = null)
    {
        this.reserveProbe = reserveProbe;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.probe = probe ?? (async (configuration, token) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var result = await processes.RunAsync(configuration.FfprobePath, ["-version"], timeout.Token);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException("ffprobe version check failed");
            }
            return await new EncoderProbe(processes).ProbeAsync(configuration, token);
        });
    }

    internal WorkerRuntimeConfiguration Current
    {
        get
        {
            lock (sync)
            {
                return current;
            }
        }
    }

    internal string? Error
    {
        get
        {
            lock (sync)
            {
                return error;
            }
        }
    }

    internal bool ConfigurationRejected
    {
        get
        {
            lock (sync)
            {
                return refreshTask.IsCompleted && error is not null;
            }
        }
    }

    internal void Request(WorkerConfiguration configuration, int revision, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (revision <= requestedRevision)
            {
                return;
            }
            requestedRevision = revision;
            Schedule(configuration, revision, false, cancellationToken);
        }
    }

    internal void RefreshCapabilitiesWhenDue(CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (current.Revision <= 0 || requestedRevision != current.Revision || !refreshTask.IsCompleted
                || hasProbed && timeProvider.GetElapsedTime(lastProbe) < TimeSpan.FromSeconds(60)
                || error is null && !NeedsCapabilityRefresh(current))
            {
                return;
            }
            Schedule(current.Configuration, current.Revision, true, cancellationToken);
        }
    }

    private static bool NeedsCapabilityRefresh(WorkerRuntimeConfiguration state)
    {
        var configuration = state.Configuration;
        var encoders = state.Capabilities.Encoders;
        var gpuMissing = configuration.GpuConcurrency > 0 && !encoders.Any(item => item.Available && item.Encoder != "libx264"
            && (configuration.PreferredEncoder == "auto" || item.Encoder == configuration.PreferredEncoder)
            && (configuration.DeviceId is null || configuration.DeviceId == item.DeviceId));
        var softwareMissing = configuration.CpuConcurrency > 0 && configuration.CpuFallbackMode == "Balanced"
            && !encoders.Any(item => item.Available && item.Encoder == "libx264");
        return gpuMissing || softwareMissing;
    }

    internal void InvalidateEncoder(EncoderCapability encoder)
    {
        if (encoder.Encoder == "libx264")
        {
            return;
        }
        lock (sync)
        {
            current = current with
            {
                Capabilities = current.Capabilities with
                {
                    Encoders = current.Capabilities.Encoders.Select(item => item.Encoder == encoder.Encoder && item.DeviceId == encoder.DeviceId
                        ? item with { Available = false, Error = "The last encoding attempt failed. Waiting to probe again" } : item with { }).ToList()
                }
            };
        }
    }

    private void Schedule(WorkerConfiguration configuration, int revision, bool forceProbe, CancellationToken cancellationToken)
    {
        var previous = refreshTask;
        refreshCancellation?.Cancel();
        refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        error = "Validating server configuration and encoder capabilities";
        refreshTask = RefreshAsync(configuration with { }, revision, forceProbe, previous, refreshCancellation);
    }

    private async Task RefreshAsync(WorkerConfiguration configuration, int revision, bool forceProbe, Task previous, CancellationTokenSource cancellation)
    {
        await Task.Yield();
        try
        {
            await previous;
            cancellation.Token.ThrowIfCancellationRequested();
            WorkerSetup.Validate(configuration);
            Directory.CreateDirectory(configuration.TempDirectory);
            var existing = Current;
            WorkerCapabilities capabilities;
            if (!forceProbe && existing.Revision > 0 && existing.Configuration.FfmpegPath == configuration.FfmpegPath
                && existing.Configuration.FfprobePath == configuration.FfprobePath
                && existing.Configuration.DeviceId == configuration.DeviceId)
            {
                capabilities = existing.Capabilities;
            }
            else
            {
                IDisposable? reservation;
                while ((reservation = reserveProbe(configuration)) is null)
                {
                    await Task.Delay(500, cancellation.Token);
                }
                using (reservation)
                {
                    capabilities = await probe(configuration, cancellation.Token);
                }
                lock (sync)
                {
                    hasProbed = true;
                    lastProbe = timeProvider.GetTimestamp();
                }
            }
            lock (sync)
            {
                if (requestedRevision == revision && !cancellation.IsCancellationRequested)
                {
                    current = new(configuration, capabilities, revision);
                    error = null;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellation.IsCancellationRequested)
        {
            lock (sync)
            {
                if (requestedRevision == revision)
                {
                    error = exception.Message;
                    hasProbed = true;
                    lastProbe = timeProvider.GetTimestamp();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(refreshCancellation, cancellation))
                {
                    refreshCancellation = null;
                }
            }
            cancellation.Dispose();
        }
    }

    internal async Task StopAsync()
    {
        Task pending;
        lock (sync)
        {
            refreshCancellation?.Cancel();
            pending = refreshTask;
        }
        await pending;
    }
}
