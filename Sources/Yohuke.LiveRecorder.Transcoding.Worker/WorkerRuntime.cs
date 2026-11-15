using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Encoding;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;
using Yohuke.LiveRecorder.Transcoding.Worker.Resources;
using Yohuke.LiveRecorder.Transcoding.Worker.Scheduling;
using Yohuke.LiveRecorder.Transcoding.Worker.Storage;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.Transcoding.Worker;

internal sealed class WorkerRuntime
{
    private readonly WorkerConnectionOptions options;
    private readonly WorkerSettingsStore settings;
    private readonly Action<string> log;
    private readonly WorkerProcessRunner processes = new();
    private readonly WorkerLogBuffer logs = new();
    private readonly CpuUsageMonitor cpu = new();
    private readonly EncodingAdmission admission = new();
    private readonly ConcurrentDictionary<string, WorkerRunningJob> jobs = new(StringComparer.Ordinal);
    private readonly WorkerConfigurationManager configurations;
    private readonly WorkerWorkspace workspace;
    private readonly WorkerFileTransfer transfers;
    private readonly MediaInspector inspector;

    internal WorkerRuntime(WorkerConnectionOptions options, WorkerSettingsStore settings, Action<string>? log = null)
    {
        this.options = options;
        this.settings = settings;
        this.log = log ?? Console.WriteLine;
        workspace = new(options.WorkerId);
        transfers = new(options);
        inspector = new(processes);
        configurations = new(processes, configuration => admission.TryAcquireProbe(configuration));
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sampling = cpu.RunAsync(lifetime.Token);
        var monitoring = MonitorLeasesAsync(lifetime.Token);
        try
        {
            var reconnectDelay = 1;
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await using var control = await WorkerControlClient.ConnectAsync(options, lifetime.Token);
                    var welcome = await control.ExchangeAsync<WorkerHello, WorkerWelcome>("Hello", "Welcome", new()
                    {
                        WorkerId = options.WorkerId,
                        Secret = options.Secret,
                        Capabilities = configurations.Current.Capabilities
                    }, lifetime.Token);
                    await settings.SaveRemoteConfigurationAsync(welcome.Configuration, welcome.ConfigurationRevision, lifetime.Token);
                    configurations.Request(welcome.Configuration, welcome.ConfigurationRevision, lifetime.Token);
                    reconnectDelay = 1;
                    Log("Connected to the server, configuration revision " + welcome.ConfigurationRevision);
                    while (!lifetime.IsCancellationRequested)
                    {
                        configurations.RefreshCapabilitiesWhenDue(lifetime.Token);
                        var current = configurations.Current;
                        var state = await control.ExchangeAsync<WorkerHeartbeat, WorkerState>("Heartbeat", "State", new()
                        {
                            AppliedConfigurationRevision = current.Revision,
                            ConfigurationError = configurations.Error,
                            Capabilities = current.Capabilities,
                            Telemetry = Telemetry(current.Configuration),
                            Jobs = jobs.Values.Select(job => job.Snapshot()).ToList(),
                            LogBatch = welcome.LogProtocolVersion >= 1 ? logs.GetBatch() : null
                        }, lifetime.Token);
                        logs.Acknowledge(state.LogAcknowledgement);
                        await settings.SaveRemoteConfigurationAsync(state.Configuration, state.ConfigurationRevision, lifetime.Token);
                        configurations.Request(state.Configuration, state.ConfigurationRevision, lifetime.Token);
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
                        foreach (var assignment in state.Assignments)
                        {
                            await StartAsync(assignment, lifetime.Token);
                        }
                        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(welcome.HeartbeatSeconds, 1, 10)), lifetime.Token);
                    }
                }
                catch (WorkerAuthenticationException)
                {
                    lifetime.Cancel();
                    throw;
                }
                catch (Exception exception) when (!lifetime.IsCancellationRequested && exception is IOException or SocketException
                    or AuthenticationException or InvalidOperationException or OperationCanceledException)
                {
                    Log("Control connection interrupted. Reconnecting: " + exception.Message);
                    await Task.Delay(TimeSpan.FromSeconds(reconnectDelay), lifetime.Token);
                    reconnectDelay = Math.Min(30, reconnectDelay * 2);
                }
            }
        }
        finally
        {
            lifetime.Cancel();
            foreach (var job in jobs.Values)
            {
                job.Cancel();
            }
            await Task.WhenAll(jobs.Values.Select(job => job.Task));
            await configurations.StopAsync();
            try
            {
                await Task.WhenAll(sampling, monitoring);
            }
            catch (OperationCanceledException)
            {
            }
            transfers.Dispose();
        }
    }

    private async Task StartAsync(TranscodeAssignment assignment, CancellationToken cancellationToken)
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
        var job = new WorkerRunningJob(assignment, cancellationToken);
        if (!jobs.TryAdd(assignment.JobId, job))
        {
            job.Dispose();
            return;
        }
        job.Task = RunJobAsync(job);
    }

    private async Task RunJobAsync(WorkerRunningJob job)
    {
        await Task.Yield();
        var token = job.Cancellation.Token;
        using var reportingCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var reporter = new WorkerJobReporter(job, WriteLog);
        var reporting = reporter.RunAsync(reportingCancellation.Token);
        try
        {
            var initial = configurations.Current;
            reporter.Start("Downloading", 0, "Preparing job, profile " + job.Assignment.Profile.Name);
            using var files = workspace.Acquire(initial.Configuration, job.Assignment, message => reporter.Message(message));
            reporter.Message("Downloading the source file.");
            await transfers.DownloadAsync(job.Assignment, files.SourcePath, initial.Configuration.DownloadBytesPerSecond,
                _ => { }, token, reporter.Transfer, message => reporter.Message(message));
            reporter.Message("Source download completed.");
            reporter.Start("Inspecting", 10, "Inspecting source media.");
            var source = await inspector.InspectInputAsync(initial.Configuration.FfprobePath, files.SourcePath, token,
                message => reporter.Message(message));
            reporter.Message(FormattableString.Invariant($"Source media inspected: {source.Width}x{source.Height}, {source.FrameRate:0.##} fps, {source.DurationMs / 1000:0.0}s."));
            _ = TranscodePlanner.Create(files.SourcePath, files.OutputDirectory, source, job.Assignment.Profile,
                new() { Encoder = "libx264", Available = true }, 1);
            var waiting = Stopwatch.StartNew();
            reporter.Start("WaitingForGpu", 15, "Waiting for an available encoder or CPU fallback capacity.");
            EncodingReservation? reservation = null;
            WorkerRuntimeConfiguration selected;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                selected = configurations.Current;
                if (configurations.ConfigurationRejected)
                {
                    reporter.Stop();
                    reporter.Message("Releasing the job because its configuration was rejected: " + configurations.Error, "Warning");
                    await transfers.ReportResultAsync(job.Assignment, new()
                    {
                        State = "Queued", ErrorCode = "WorkerCapacityUnavailable", Error = configurations.Error
                    }, token);
                    return;
                }
                if (configurations.Error is null)
                {
                    var resources = cpu.Snapshot(selected.Configuration.CpuSampleWindowSeconds);
                    if (!EncodingAdmission.HasPossibleCapacity(selected.Configuration, selected.Capabilities, resources)
                        && (selected.Revision != initial.Revision || waiting.Elapsed > TimeSpan.FromSeconds(
                            selected.Configuration.GpuWaitSeconds + selected.Configuration.CpuSampleWindowSeconds)))
                    {
                        reporter.Stop();
                        reporter.Message("Releasing the job to wait for available resources.", "Warning");
                        await transfers.ReportResultAsync(job.Assignment, new()
                        {
                            State = "Queued", ErrorCode = "WorkerCapacityUnavailable", Error = "Worker configuration or device capabilities changed. Releasing the job to wait for an available worker."
                        }, token);
                        return;
                    }
                    reservation = admission.TryAcquire(selected.Configuration, selected.Capabilities,
                        resources, waiting.Elapsed);
                }
                if (reservation is not null)
                {
                    break;
                }
                await Task.Delay(250, token);
            }
            TranscodeOutputManifest manifest;
            using (reservation)
            {
                var plan = TranscodePlanner.Create(files.SourcePath, files.OutputDirectory, source,
                    job.Assignment.Profile, reservation.Encoder, reservation.Threads);
                reporter.Start("Encoding", 15, FormattableString.Invariant($"Starting encode with {reservation.Encoder.Encoder}, thread budget {reservation.Threads}."), reservation.Encoder.Encoder);
                var result = await processes.RunAsync(selected.Configuration.FfmpegPath, plan.Arguments, token,
                    errorLine: message => reporter.Message("FFmpeg: " + message, "Error"),
                    encodingProgress: value => reporter.Encoding(value, source.DurationMs));
                if (result.ExitCode != 0)
                {
                    configurations.InvalidateEncoder(reservation.Encoder);
                    throw new InvalidDataException("FFmpeg encoding failed: " + result.Diagnostic);
                }
                reporter.Start("Validating", 80, "Encoding completed. Validating output media, fragment timelines and checksums.");
                manifest = await new TranscodeOutputValidator(inspector).ValidateAsync(selected.Configuration.FfprobePath,
                    files.OutputDirectory, source, plan.ExpectedMedia, reservation.Encoder.Encoder, token, job.Assignment.Profile);
            }
            reporter.Start("Uploading", 85, "Output validation completed. Uploading the result.");
            await transfers.UploadAsync(job.Assignment, files.OutputDirectory, manifest, selected.Configuration.UploadBytesPerSecond,
                _ => { }, token, reporter.Transfer, message => reporter.Message(message),
                () => reporter.Start("Finalizing", 99, "Upload completed. Waiting for server verification and publication."));
            reporter.Stop();
            reporter.Message("Completed job. The server verified and published the result.");
        }
        catch (OperationCanceledException)
        {
            reporter.Stop();
            reporter.Message("Stopped job.", "Warning");
        }
        catch (Exception exception)
        {
            reporter.Stop();
            reporter.Message("Job failed: " + exception.Message, "Error");
            try
            {
                await transfers.ReportResultAsync(job.Assignment, new()
                {
                    State = exception is NotSupportedException ? "Skipped" : "Failed",
                    ErrorCode = exception is NotSupportedException ? exception.Message : "WorkerExecutionFailed",
                    Error = exception.Message.Length > 4000 ? exception.Message[..4000] : exception.Message
                }, token);
            }
            catch (Exception reportError) when (reportError is HttpRequestException or IOException or OperationCanceledException)
            {
                reporter.Message("Unable to report the final job state. Waiting for the server to reclaim the lease: " + reportError.Message, "Warning");
            }
        }
        finally
        {
            await reportingCancellation.CancelAsync();
            try
            {
                await reporting;
            }
            catch (OperationCanceledException) when (reportingCancellation.IsCancellationRequested)
            {
            }
            jobs.TryRemove(new KeyValuePair<string, WorkerRunningJob>(job.Assignment.JobId, job));
            job.Dispose();
        }
    }

    private WorkerTelemetry Telemetry(WorkerConfiguration configuration)
    {
        var snapshot = cpu.Snapshot(configuration.CpuSampleWindowSeconds);
        var running = admission.Running;
        var free = 0L;
        try
        {
            if (!string.IsNullOrWhiteSpace(configuration.TempDirectory))
            {
                free = WorkerWorkspace.FreeBytes(configuration.TempDirectory);
            }
        }
        catch (IOException)
        {
        }
        return new()
        {
            CpuPercent = snapshot.AveragePercent,
            CpuThreadBudget = EncodingAdmission.ThreadBudget(configuration, snapshot),
            CpuUnavailableReason = snapshot.UnavailableReason,
            RunningCpu = running.Cpu,
            RunningGpu = running.Gpu,
            FreeBytes = free
        };
    }

    private async Task MonitorLeasesAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            foreach (var job in jobs.Values)
            {
                job.CheckLease();
            }
        }
    }

    private void Log(string message) => WriteLog(new() { Message = message });

    private void WriteLog(WorkerLogEntry entry)
    {
        logs.Append(entry);
        var scope = entry.JobId is null ? "" : $" [Job {entry.JobId}, attempt {entry.Attempt}]";
        var level = entry.Level == "Information" ? "" : " " + entry.Level;
        log($"[{DateTimeOffset.Now:HH:mm:ss}]{level}{scope} {entry.Message}");
    }
}
