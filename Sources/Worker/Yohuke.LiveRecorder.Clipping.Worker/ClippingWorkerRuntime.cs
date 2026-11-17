using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using Yohuke.LiveRecorder.Worker.Common.Stages;
using Yohuke.LiveRecorder.Worker.Common.Storage;
using Yohuke.LiveRecorder.Clipping.Worker.Stages;
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
    private readonly Dictionary<string, WorkerCheckpointLocation<ClipAssignment, ClipCheckpointPayload>> recoveries = new(StringComparer.Ordinal);
    private readonly WorkerCheckpointStore<ClipAssignment, ClipCheckpointPayload> checkpoints = new(settings.Path, options.WorkerId,
        "live-recorder-clipping-worker", message => (log ?? Console.WriteLine)(message));
    private bool supportsRecovery;
    private readonly ConcurrentDictionary<string, IDisposable> recoveryProtections = new(StringComparer.Ordinal);

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var workspace = new ClipWorkspace(options.WorkerId, message => WriteLog(message, "Warning"));
        using var transfer = new ClipFileTransfer(options);
        var monitoring = Task.CompletedTask;
        try
        {
            if (Path.IsPathFullyQualified(settings.Current.Configuration.TempDirectory))
            {
                checkpoints.RegisterRoot(settings.Current.Configuration.TempDirectory);
            }
            foreach (var location in checkpoints.ReadAll().Where(value => value.Checkpoint.State is "Running" or "Interrupted" or "Reporting" or "AwaitingPublication")
                .GroupBy(value => value.Checkpoint.JobId, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(value => value.Checkpoint.LastAccessAtUtc).First()))
            {
                recoveryProtections[location.Checkpoint.JobId] = workspace.ProtectCheckpointDirectory(Path.GetDirectoryName(location.Path)!);
                recoveries[location.Checkpoint.JobId] = location;
            }
            monitoring = MonitorAsync(lifetime.Token);
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
                    supportsRecovery = welcome.RecoveryProtocolVersion >= 1;
                    if (!supportsRecovery)
                    {
                        recoveries.Clear();
                        foreach (var id in recoveryProtections.Keys)
                        {
                            ReleaseRecoveryProtection(id);
                        }
                    }
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
                            RecoveryRequests = RecoveryRequests(),
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
                        await RecoverAsync(state.RecoveryResults, workspace, transfer, lifetime.Token);
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
                catch (Exception error) when (lifetime.IsCancellationRequested
                    && error is IOException or SocketException or AuthenticationException or OperationCanceledException)
                {
                    break;
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
            try
            {
                await Task.WhenAll(jobs.Values.Select(job => job.Task));
            }
            finally
            {
                foreach (var id in recoveryProtections.Keys)
                {
                    ReleaseRecoveryProtection(id);
                }
                try
                {
                    await monitoring;
                }
                catch (OperationCanceledException)
                {
                }
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

    private List<WorkerJobRecoveryRequest> RecoveryRequests()
    {
        var result = new List<WorkerJobRecoveryRequest>();
        var executionSlots = Math.Max(0, configuration.MaxConcurrentEncodes - jobs.Values.Count(job => !job.IsReportingOnly));
        var passive = recoveries.Values.Where(value => value.Checkpoint.PendingResult is not null
            || value.Checkpoint.State == "AwaitingPublication" || value.Checkpoint.Payload.PublicationPending);
        var executing = recoveries.Values.Where(value => value.Checkpoint.PendingResult is null
            && value.Checkpoint.State != "AwaitingPublication" && !value.Checkpoint.Payload.PublicationPending).Take(executionSlots);
        foreach (var location in passive.Concat(executing).Take(Math.Max(0, 65 - jobs.Count)))
        {
            var checkpoint = location.Checkpoint;
            if (checkpoint.NextLeaseToken is null)
            {
                checkpoint.NextLeaseToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                try
                {
                    WorkerCheckpointStore<ClipAssignment, ClipCheckpointPayload>.Save(location);
                }
                catch
                {
                    checkpoint.NextLeaseToken = null;
                    throw;
                }
            }
            result.Add(new()
            {
                JobId = checkpoint.JobId, Attempt = checkpoint.Assignment.Attempt,
                LeaseToken = checkpoint.Assignment.LeaseToken, NextLeaseToken = checkpoint.NextLeaseToken
            });
        }
        return result;
    }

    private async Task RecoverAsync(IReadOnlyList<WorkerJobRecoveryStatus> statuses, ClipWorkspace workspace,
        ClipFileTransfer transfer, CancellationToken cancellationToken)
    {
        foreach (var status in statuses)
        {
            if (!recoveries.TryGetValue(status.JobId, out var location) || location.Checkpoint.Assignment.Attempt != status.Attempt)
            {
                continue;
            }
            var checkpoint = location.Checkpoint;
            if (status.Disposition == "Resume")
            {
                checkpoint.Assignment = checkpoint.Assignment with { LeaseToken = checkpoint.NextLeaseToken!, LeaseSeconds = status.LeaseSeconds };
                checkpoint.NextLeaseToken = null;
                checkpoint.Payload.PublicationPending = status.State == "Completed" && checkpoint.Payload.Manifest is not null;
                if (checkpoint.Payload.PublicationPending)
                {
                    checkpoint.PendingResult = null;
                }
                if (status.State == "Completed" && checkpoint.Payload.Manifest is null)
                {
                    checkpoint.State = "AwaitingPublication";
                    checkpoint.PendingResult = null;
                    WorkerCheckpointStore<ClipAssignment, ClipCheckpointPayload>.Save(location);
                    continue;
                }
                WorkerCheckpointStore<ClipAssignment, ClipCheckpointPayload>.Save(location);
                await StartAsync(checkpoint.Assignment, workspace, transfer, cancellationToken, location);
            }
            else if (status.Disposition == "Completed")
            {
                Directory.Delete(Path.GetDirectoryName(location.Path)!, recursive: true);
            }
            else
            {
                checkpoint.State = "Rejected";
                checkpoint.NextLeaseToken = null;
                WorkerCheckpointStore<ClipAssignment, ClipCheckpointPayload>.Save(location);
            }
            recoveries.Remove(status.JobId);
            if (!jobs.ContainsKey(status.JobId))
            {
                ReleaseRecoveryProtection(status.JobId);
            }
        }
    }

    private void ReleaseRecoveryProtection(string jobId)
    {
        if (recoveryProtections.TryRemove(jobId, out var protection))
        {
            protection.Dispose();
        }
    }

    private async Task StartAsync(ClipAssignment assignment, ClipWorkspace workspace, ClipFileTransfer transfer,
        CancellationToken cancellationToken, WorkerCheckpointLocation<ClipAssignment, ClipCheckpointPayload>? recovered = null)
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
        var reportingOnly = recovered is not null && (recovered.Checkpoint.PendingResult is not null || recovered.Checkpoint.Payload.PublicationPending);
        if (!reportingOnly && (configurationError is not null || jobs.Values.Count(job => !job.IsReportingOnly) >= configuration.MaxConcurrentEncodes))
        {
            await transfer.ReportResultAsync(assignment, new()
            {
                State = "Queued", ErrorCode = "WorkerCapacityUnavailable", Error = configurationError ?? "The clipping worker is at capacity."
            }, cancellationToken);
            return;
        }
        if (recovered is null)
        {
            recoveries.Remove(assignment.JobId);
        }
        var job = new ClipRunningJob(assignment, cancellationToken) { IsReportingOnly = reportingOnly };
        if (!jobs.TryAdd(assignment.JobId, job))
        {
            job.Dispose();
            return;
        }
        job.Task = RunJobAsync(job, configuration with { }, workspace, transfer, recovered);
    }

    private async Task RunJobAsync(ClipRunningJob job, WorkerConfiguration selected, ClipWorkspace workspace, ClipFileTransfer transfer,
        WorkerCheckpointLocation<ClipAssignment, ClipCheckpointPayload>? recovered)
    {
        await Task.Yield();
        var token = job.Cancellation.Token;
        WorkerCheckpointLocation<ClipAssignment, ClipCheckpointPayload>? location = recovered;
        try
        {
            location ??= checkpoints.Open(selected.TempDirectory, job.Assignment.JobId, ClipContentIdentity.Create(job.Assignment), job.Assignment);
            if (recovered is null)
            {
                location.Checkpoint.Payload.PublicationPending = false;
            }
            if (location.Checkpoint.PendingResult is { } pending)
            {
                job.IsReportingOnly = true;
                job.SetStages(pending.Stages);
                await ReportResultUntilConfirmedAsync(job, location, pending, transfer);
                return;
            }
            if (location.Checkpoint.Payload.PublicationPending)
            {
                job.SetStages(location.Checkpoint.Stages);
                job.Update("Finalizing", 99);
                await transfer.CompleteAsync(job.Assignment, location.Checkpoint.Payload.Manifest!, token,
                    message => WriteLog(message, job: job));
                location.Checkpoint.State = "Completed";
                WorkerCheckpointStore<ClipAssignment, ClipCheckpointPayload>.Save(location);
                Directory.Delete(Path.GetDirectoryName(location.Path)!, recursive: true);
                return;
            }
            using var files = workspace.Acquire(selected, job.Assignment, Path.GetDirectoryName(location.Path));
            ReleaseRecoveryProtection(job.Assignment.JobId);
            var context = new ClipStageContext(location, files, selected, transfer, job,
                message => WriteLog(message, job: job), supportsRecovery);
            job.PulseStages = context.Pulse;
            await new WorkerStageRunner<ClipStageContext>([
                new ClipDownloadStage(), new ClipPrepareStage(), new ClipExportStage(),
                new ClipValidateStage(), new ClipUploadStage(), new ClipFinalizeStage()
            ]).RunAsync(context, token);
            location.Checkpoint.State = "Completed";
            context.Save();
            files.Complete();
            WriteLog("The server verified and published the clip. Cleaning the completed workspace.", job: job);
        }
        catch (OperationCanceledException)
        {
            if (location is not null)
            {
                location.Checkpoint.State = "Interrupted";
                WorkerCheckpointStore<ClipAssignment, ClipCheckpointPayload>.Save(location);
            }
            WriteLog("Stopped clipping task; completed stages remain available for recovery.", "Warning", job);
        }
        catch (Exception error)
        {
            WriteLog("Clipping task stopped: " + error.Message, "Error", job);
            var result = new WorkerJobResult
            {
                State = error is ClipCapacityException ? "Queued" : "Failed",
                ErrorCode = error is ClipCapacityException ? "WorkerCapacityUnavailable" : "WorkerExecutionFailed",
                Error = error.Message.Length > 4000 ? error.Message[..4000] : error.Message,
                Stages = job.Snapshot().Stages
            };
            if (location is not null)
            {
                location.Checkpoint.State = "Reporting";
                location.Checkpoint.PendingResult = result;
                WorkerCheckpointStore<ClipAssignment, ClipCheckpointPayload>.Save(location);
            }
            job.IsReportingOnly = true;
            await ReportResultUntilConfirmedAsync(job, location, result, transfer);
        }
        finally
        {
            job.PulseStages = null;
            ReleaseRecoveryProtection(job.Assignment.JobId);
            jobs.TryRemove(new KeyValuePair<string, ClipRunningJob>(job.Assignment.JobId, job));
            job.Dispose();
        }
    }

    private async Task ReportResultUntilConfirmedAsync(ClipRunningJob job,
        WorkerCheckpointLocation<ClipAssignment, ClipCheckpointPayload>? location, WorkerJobResult result,
        ClipFileTransfer transfer)
    {
        var token = job.Cancellation.Token;
        var delay = 1;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await transfer.ReportResultAsync(job.Assignment, result, token);
                if (location is not null)
                {
                    location.Checkpoint.State = result.State;
                    location.Checkpoint.PendingResult = null;
                    WorkerCheckpointStore<ClipAssignment, ClipCheckpointPayload>.Save(location);
                }
                return;
            }
            catch (WorkerLeaseLostException error)
            {
                if (location is not null)
                {
                    location.Checkpoint.State = "Rejected";
                    WorkerCheckpointStore<ClipAssignment, ClipCheckpointPayload>.Save(location);
                }
                WriteLog("The pending result lost its execution lease: " + error.Message, "Warning", job);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
            {
                WriteLog("The final state remains pending; control heartbeats will continue: " + error.Message, "Warning", job);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                delay = Math.Min(30, delay * 2);
            }
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
                job.PulseStages?.Invoke();
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
