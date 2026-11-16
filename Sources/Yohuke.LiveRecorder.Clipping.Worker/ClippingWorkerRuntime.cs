using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Clipping.Worker.Execution;
using Yohuke.LiveRecorder.Clipping.Worker.Scheduling;
using Yohuke.LiveRecorder.Clipping.Worker.Storage;
using Yohuke.LiveRecorder.Clipping.Worker.Transport;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.Clipping.Worker;

internal sealed class ClippingWorkerRuntime(WorkerConnectionOptions options, WorkerSettingsStore settings, Action<string>? log = null)
{
    private readonly Action<string> log = log ?? Console.WriteLine;
    private readonly ConcurrentDictionary<string, ClipRunningJob> jobs = new(StringComparer.Ordinal);
    private readonly WorkerLogBuffer logs = new();
    private readonly WorkerProcessRunner tools = new();
    private WorkerConfiguration configuration = new();
    private WorkerCapabilities capabilities = new();
    private int appliedRevision;
    private int requestedRevision = -1;
    private DateTime lastProbeUtc;
    private string? configurationError;

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var workspace = new ClipWorkspace(options.WorkerId, message => WriteLog(message, "Warning"));
        using var transfer = new ClipFileTransfer(options);
        var monitoring = MonitorAsync(lifetime.Token);
        try
        {
            var delay = 1;
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await using var control = await WorkerControlClient.ConnectAsync(options, lifetime.Token);
                    var welcome = await control.ExchangeAsync<WorkerHello, WorkerWelcome>("Hello", "Welcome", new()
                    {
                        Kind = "Clipping", WorkerId = options.WorkerId, Secret = options.Secret, Capabilities = capabilities
                    }, lifetime.Token);
                    await ApplyConfigurationAsync(welcome.Configuration, welcome.ConfigurationRevision, workspace, lifetime.Token);
                    delay = 1;
                    WriteLog("Connected to the clipping coordinator.");
                    while (!lifetime.IsCancellationRequested)
                    {
                        var freeBytes = 0L;
                        try
                        {
                            freeBytes = workspace.FreeBytes(configuration.TempDirectory);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                        {
                            configurationError = error.Message;
                        }
                        var state = await control.ExchangeAsync<WorkerHeartbeat, WorkerState>("Heartbeat", "State", new()
                        {
                            AppliedConfigurationRevision = appliedRevision,
                            ConfigurationError = configurationError,
                            Capabilities = capabilities,
                            Telemetry = new() { FreeBytes = freeBytes, RunningCpu = jobs.Count },
                            Jobs = jobs.Values.Select(job => job.Snapshot()).ToList(),
                            LogBatch = welcome.LogProtocolVersion >= 1 ? logs.GetBatch() : null
                        }, lifetime.Token);
                        logs.Acknowledge(state.LogAcknowledgement);
                        await ApplyConfigurationAsync(state.Configuration, state.ConfigurationRevision, workspace, lifetime.Token);
                        foreach (var lease in state.Leases)
                        {
                            if (jobs.TryGetValue(lease.JobId, out var job) && job.Assignment.Attempt == lease.Attempt)
                            {
                                if (lease.Accepted)
                                {
                                    job.Renew(lease.LeaseSeconds);
                                }
                                else
                                {
                                    job.Cancel();
                                }
                            }
                        }
                        foreach (var assignment in state.ClipAssignments)
                        {
                            await StartAsync(assignment, workspace, transfer, lifetime.Token);
                        }
                        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(welcome.HeartbeatSeconds, 1, 10)), lifetime.Token);
                    }
                }
                catch (WorkerAuthenticationException)
                {
                    await lifetime.CancelAsync();
                    throw;
                }
                catch (Exception error) when (!lifetime.IsCancellationRequested
                    && error is IOException or SocketException or AuthenticationException or InvalidOperationException or OperationCanceledException)
                {
                    WriteLog("Clipping control connection interrupted. Reconnecting: " + error.Message, "Warning");
                    await Task.Delay(TimeSpan.FromSeconds(delay), lifetime.Token);
                    delay = Math.Min(30, delay * 2);
                }
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            foreach (var job in jobs.Values)
            {
                job.Cancel();
            }
            await Task.WhenAll(jobs.Values.Select(job => job.Task));
            try
            {
                await monitoring;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task ApplyConfigurationAsync(WorkerConfiguration value, int revision, ClipWorkspace workspace, CancellationToken cancellationToken)
    {
        await settings.SaveRemoteConfigurationAsync(value, revision, cancellationToken);
        if (requestedRevision == revision && DateTime.UtcNow - lastProbeUtc < TimeSpan.FromMinutes(1))
        {
            return;
        }
        requestedRevision = revision;
        lastProbeUtc = DateTime.UtcNow;
        try
        {
            WorkerConfigurationValidation.Validate(value, "Clipping");
            _ = workspace.FreeBytes(value.TempDirectory);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var encoder = await tools.RunAsync(value.FfmpegPath, ["-hide_banner", "-encoders"], timeout.Token);
            var probe = await tools.RunAsync(value.FfprobePath, ["-version"], timeout.Token);
            if (encoder.ExitCode != 0 || probe.ExitCode != 0 || !encoder.Output.Contains("libx264", StringComparison.Ordinal)
                || !encoder.Output.Contains(" aac ", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Clipping requires FFmpeg with libx264 and AAC encoders, and ffprobe.");
            }
            configuration = value with { };
            appliedRevision = revision;
            configurationError = null;
            capabilities = new()
            {
                ClippingVersion = 1,
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                ToolVersion = probe.Output.Split('\n')[0].Trim()
            };
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            configurationError = error.Message;
            capabilities = capabilities with { ClippingVersion = 0 };
            WriteLog("Clipping configuration is unavailable: " + error.Message, "Error");
        }
    }

    private async Task StartAsync(ClipAssignment assignment, ClipWorkspace workspace, ClipFileTransfer transfer, CancellationToken cancellationToken)
    {
        if (jobs.TryGetValue(assignment.JobId, out var existing))
        {
            if (existing.Assignment.Attempt == assignment.Attempt && existing.Assignment.LeaseToken == assignment.LeaseToken)
            {
                return;
            }
            existing.Cancel();
            await existing.Task;
        }
        if (configurationError is not null || jobs.Count >= configuration.MaxConcurrentEncodes)
        {
            await transfer.ReportResultAsync(assignment, new()
            {
                State = "Queued", ErrorCode = "WorkerCapacityUnavailable", Error = configurationError ?? "The clipping worker is at capacity."
            }, cancellationToken);
            return;
        }
        var job = new ClipRunningJob(assignment, cancellationToken);
        if (!jobs.TryAdd(assignment.JobId, job))
        {
            job.Dispose();
            return;
        }
        job.Task = RunJobAsync(job, configuration with { }, workspace, transfer);
    }

    private async Task RunJobAsync(ClipRunningJob job, WorkerConfiguration selected, ClipWorkspace workspace, ClipFileTransfer transfer)
    {
        await Task.Yield();
        var token = job.Cancellation.Token;
        try
        {
            using var files = workspace.Acquire(selected, job.Assignment);
            job.Update("Downloading", 0);
            WriteLog("Downloading only the assigned clipping input ranges.", job: job);
            var manifest = await new ClipExecutor(transfer).ExecuteAsync(job.Assignment, selected, files, token,
                (bytes, total) => job.Update("Downloading", total > 0 ? (double)bytes / total * 15 : 15,
                    new() { BytesTransferred = bytes, TotalBytes = total }),
                progress => job.Update("Encoding", 15 + progress * 65,
                    new() { ProcessedDurationMs = progress * (job.Assignment.ActualEndMs - job.Assignment.ActualStartMs),
                        TotalDurationMs = job.Assignment.ActualEndMs - job.Assignment.ActualStartMs }),
                message => WriteLog(message, job: job));
            job.Update("Uploading", 80);
            WriteLog("Clip exported and validated. Uploading the MP4.", job: job);
            await transfer.UploadAsync(job.Assignment, files.OutputPath, manifest, selected.UploadBytesPerSecond, token,
                bytes => job.Update("Uploading", 80 + (double)bytes / manifest.Bytes * 19,
                    new() { BytesTransferred = bytes, TotalBytes = manifest.Bytes }),
                message => WriteLog(message, job: job),
                () => job.Update("Finalizing", 99));
            WriteLog("The server verified and published the clip. Cleaning the task workspace.", job: job);
        }
        catch (WorkerLeaseLostException error)
        {
            WriteLog("Clipping lease was revoked: " + error.Message, "Warning", job);
        }
        catch (OperationCanceledException)
        {
            WriteLog("Stopped clipping task. Cleaning the task workspace.", "Warning", job);
        }
        catch (ClipCapacityException error)
        {
            WriteLog("Returning the clipping task to wait for available workspace capacity.", "Warning", job);
            try
            {
                await transfer.ReportResultAsync(job.Assignment, new()
                {
                    State = "Queued", ErrorCode = "WorkerCapacityUnavailable", Error = error.Message
                }, token);
            }
            catch (Exception reportingError)
            {
                WriteLog("Unable to release the clipping task: " + reportingError.Message, "Warning", job);
            }
        }
        catch (Exception error)
        {
            WriteLog("Clipping task failed: " + error.Message, "Error", job);
            try
            {
                await transfer.ReportResultAsync(job.Assignment, new()
                {
                    State = "Failed", ErrorCode = "WorkerExecutionFailed",
                    Error = error.Message.Length > 4000 ? error.Message[..4000] : error.Message
                }, token);
            }
            catch (Exception reportingError)
            {
                WriteLog("Unable to report the clipping result: " + reportingError.Message, "Warning", job);
            }
        }
        finally
        {
            jobs.TryRemove(new KeyValuePair<string, ClipRunningJob>(job.Assignment.JobId, job));
            job.Dispose();
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var ticks = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            foreach (var job in jobs.Values)
            {
                job.CheckLease();
                if (ticks % 5 == 0)
                {
                    var progress = job.Snapshot();
                    WriteLog(FormattableString.Invariant($"{progress.Stage}: {progress.Progress:0.0}%"), job: job);
                }
            }
            ticks++;
        }
    }

    private void WriteLog(string message, string level = "Information", ClipRunningJob? job = null)
    {
        var progress = job?.Snapshot();
        logs.Append(new()
        {
            Message = message, Level = level, JobId = job?.Assignment.JobId, Attempt = job?.Assignment.Attempt,
            Stage = progress?.Stage, Progress = progress?.Progress
        });
        log(DateTime.UtcNow.ToString("O") + " " + level + " " + message);
    }
}
