using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Yohuke.LiveRecorder.Worker.Common.Stages;
using Yohuke.LiveRecorder.Worker.Common.Storage;
using Yohuke.LiveRecorder.Transcoding.Worker.Stages;
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
    private readonly WorkerCheckpointStore<TranscodeAssignment, TranscodeCheckpointPayload> checkpoints;
    private readonly Dictionary<string, WorkerCheckpointLocation<TranscodeAssignment, TranscodeCheckpointPayload>> recoveries = new(StringComparer.Ordinal);
    private bool supportsRecovery;
    private readonly ConcurrentDictionary<string, IDisposable> recoveryProtections = new(StringComparer.Ordinal);

    internal WorkerRuntime(WorkerConnectionOptions options, WorkerSettingsStore settings, Action<string>? log = null)
    {
        this.options = options;
        this.settings = settings;
        this.log = log ?? Console.WriteLine;
        workspace = new(options.WorkerId);
        transfers = new(options);
        inspector = new(processes);
        configurations = new(processes, configuration => admission.TryAcquireProbe(configuration));
        checkpoints = new(settings.Path, options.WorkerId, "live-recorder-worker", Log);
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sampling = Task.CompletedTask;
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
                recoveryProtections[location.Checkpoint.JobId] = workspace.ProtectCheckpointDirectory(Path.GetDirectoryName(location.Path)!, location.Checkpoint.Assignment);
                recoveries[location.Checkpoint.JobId] = location;
            }
            sampling = cpu.RunAsync(lifetime.Token);
            monitoring = MonitorLeasesAsync(lifetime.Token);
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
                    supportsRecovery = welcome.RecoveryProtocolVersion >= 1;
                    if (!supportsRecovery)
                    {
                        recoveries.Clear();
                        foreach (var id in recoveryProtections.Keys)
                        {
                            ReleaseRecoveryProtection(id);
                        }
                    }
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
                            RecoveryRequests = RecoveryRequests(),
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
                        await RecoverAsync(state.RecoveryResults, lifetime.Token);
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
                catch (Exception exception) when (lifetime.IsCancellationRequested && exception is IOException or SocketException
                    or AuthenticationException or OperationCanceledException)
                {
                    break;
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
                await configurations.StopAsync();
                try
                {
                    await Task.WhenAll(sampling, monitoring);
                }
                catch (OperationCanceledException)
                {
                }
                transfers.Dispose();
                workspace.Dispose();
            }
        }
    }

    private List<WorkerJobRecoveryRequest> RecoveryRequests()
    {
        var result = new List<WorkerJobRecoveryRequest>();
        var executionSlots = Math.Max(0, settings.Current.Configuration.MaxConcurrentEncodes - jobs.Values.Count(job => !job.IsReportingOnly));
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
                    WorkerCheckpointStore<TranscodeAssignment, TranscodeCheckpointPayload>.Save(location);
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

    private async Task RecoverAsync(IReadOnlyList<WorkerJobRecoveryStatus> statuses, CancellationToken cancellationToken)
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
                checkpoint.Assignment.LeaseToken = checkpoint.NextLeaseToken!;
                checkpoint.Assignment.LeaseSeconds = status.LeaseSeconds;
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
                    WorkerCheckpointStore<TranscodeAssignment, TranscodeCheckpointPayload>.Save(location);
                    continue;
                }
                WorkerCheckpointStore<TranscodeAssignment, TranscodeCheckpointPayload>.Save(location);
                await StartAsync(checkpoint.Assignment, cancellationToken, location);
            }
            else if (status.Disposition == "Completed")
            {
                Directory.Delete(Path.GetDirectoryName(location.Path)!, recursive: true);
            }
            else
            {
                checkpoint.State = "Rejected";
                checkpoint.NextLeaseToken = null;
                WorkerCheckpointStore<TranscodeAssignment, TranscodeCheckpointPayload>.Save(location);
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

    private async Task StartAsync(TranscodeAssignment assignment, CancellationToken cancellationToken,
        WorkerCheckpointLocation<TranscodeAssignment, TranscodeCheckpointPayload>? recovered = null)
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
        if (recovered is null)
        {
            recoveries.Remove(assignment.JobId);
        }
        var job = new WorkerRunningJob(assignment, cancellationToken)
        {
            IsReportingOnly = recovered is not null && (recovered.Checkpoint.PendingResult is not null || recovered.Checkpoint.Payload.PublicationPending)
        };
        if (!jobs.TryAdd(assignment.JobId, job))
        {
            job.Dispose();
            return;
        }
        job.Task = RunJobAsync(job, recovered);
    }

    private async Task RunJobAsync(WorkerRunningJob job, WorkerCheckpointLocation<TranscodeAssignment, TranscodeCheckpointPayload>? recovered)
    {
        await Task.Yield();
        var token = job.Cancellation.Token;
        using var reportingCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var reporter = new WorkerJobReporter(job, WriteLog);
        var reporting = reporter.RunAsync(reportingCancellation.Token);
        WorkerCheckpointLocation<TranscodeAssignment, TranscodeCheckpointPayload>? location = recovered;
        try
        {
            var configuration = configurations.Current.Revision > 0 ? configurations.Current.Configuration : settings.Current.Configuration;
            location ??= checkpoints.Open(configuration.TempDirectory, job.Assignment.JobId, TranscodeContentIdentity.Create(job.Assignment), job.Assignment);
            if (recovered is null)
            {
                location.Checkpoint.Payload.PublicationPending = false;
                location.Checkpoint.Payload.EncodingAttempts = 0;
            }
            if (location.Checkpoint.PendingResult is { } pending)
            {
                job.IsReportingOnly = true;
                job.SetStages(pending.Stages);
                await ReportResultUntilConfirmedAsync(job, location, pending, reporter);
                return;
            }
            if (location.Checkpoint.Payload.PublicationPending)
            {
                job.SetStages(location.Checkpoint.Stages);
                reporter.Start("Finalizing", 99, "Resuming server publication from the persisted manifest.");
                await transfers.CompleteAsync(job.Assignment, location.Checkpoint.Payload.Manifest!, token, message => reporter.Message(message));
                location.Checkpoint.State = "Completed";
                WorkerCheckpointStore<TranscodeAssignment, TranscodeCheckpointPayload>.Save(location);
                Directory.Delete(Path.GetDirectoryName(location.Path)!, recursive: true);
                return;
            }
            using var files = workspace.Acquire(configuration, job.Assignment, message => reporter.Message(message), Path.GetDirectoryName(location.Path));
            ReleaseRecoveryProtection(job.Assignment.JobId);
            var context = new TranscodeStageContext(location, files, configuration, transfers, inspector,
                new(processes, configurations.InvalidateEncoder), job, reporter,
                cancellation => ReserveAsync(reporter, cancellation), supportsRecovery);
            job.PulseStages = context.Pulse;
            await new WorkerStageRunner<TranscodeStageContext>([
                new TranscodeDownloadStage(), new TranscodeInspectStage(), new TranscodeEncodeStage(),
                new TranscodeValidateStage(), new TranscodeUploadStage(), new TranscodeFinalizeStage()
            ]).RunAsync(context, token);
            location.Checkpoint.State = "Completed";
            context.Save();
            files.Complete();
            reporter.Message("Completed job. The server verified and published the result.");
        }
        catch (OperationCanceledException)
        {
            if (location is not null)
            {
                location.Checkpoint.State = "Interrupted";
                WorkerCheckpointStore<TranscodeAssignment, TranscodeCheckpointPayload>.Save(location);
            }
            reporter.Message("Stopped job; its completed stages were preserved.", "Warning");
        }
        catch (Exception exception)
        {
            reporter.Message("Job failed: " + exception.Message, "Error");
            var result = new WorkerJobResult
            {
                State = exception is WorkerCapacityUnavailableException ? "Queued" : exception is NotSupportedException ? "Skipped" : "Failed",
                ErrorCode = exception is WorkerCapacityUnavailableException ? "WorkerCapacityUnavailable"
                    : exception is NotSupportedException ? exception.Message : "WorkerExecutionFailed",
                Error = exception.Message.Length > 4000 ? exception.Message[..4000] : exception.Message,
                Stages = job.Snapshot().Stages
            };
            if (location is not null)
            {
                location.Checkpoint.PendingResult = result;
                location.Checkpoint.State = "Reporting";
                WorkerCheckpointStore<TranscodeAssignment, TranscodeCheckpointPayload>.Save(location);
            }
            job.IsReportingOnly = true;
            await ReportResultUntilConfirmedAsync(job, location, result, reporter);
        }
        finally
        {
            reporter.Stop();
            job.PulseStages = null;
            ReleaseRecoveryProtection(job.Assignment.JobId);
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

    private async Task<(WorkerRuntimeConfiguration Configuration, EncodingReservation Reservation)> ReserveAsync(
        WorkerJobReporter reporter, CancellationToken cancellationToken)
    {
        var initial = configurations.Current;
        var waiting = Stopwatch.StartNew();
        reporter.Message("Waiting for an available encoder or CPU fallback capacity.");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = configurations.Current;
            if (configurations.ConfigurationRejected)
            {
                throw new WorkerCapacityUnavailableException(configurations.Error ?? "The worker configuration was rejected.");
            }
            if (configurations.Error is null)
            {
                var resources = cpu.Snapshot(selected.Configuration.CpuSampleWindowSeconds);
                if (!EncodingAdmission.HasPossibleCapacity(selected.Configuration, selected.Capabilities, resources)
                    && (selected.Revision != initial.Revision || waiting.Elapsed > TimeSpan.FromSeconds(
                        selected.Configuration.GpuWaitSeconds + selected.Configuration.CpuSampleWindowSeconds)))
                {
                    throw new WorkerCapacityUnavailableException("The worker is waiting for compatible encoder capacity.");
                }
                var reservation = admission.TryAcquire(selected.Configuration, selected.Capabilities, resources, waiting.Elapsed);
                if (reservation is not null)
                {
                    return (selected, reservation);
                }
            }
            await Task.Delay(250, cancellationToken);
        }
    }

    private async Task ReportResultUntilConfirmedAsync(WorkerRunningJob job,
        WorkerCheckpointLocation<TranscodeAssignment, TranscodeCheckpointPayload>? location, WorkerJobResult result,
        WorkerJobReporter reporter)
    {
        var token = job.Cancellation.Token;
        var delay = 1;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await transfers.ReportResultAsync(job.Assignment, result, token);
                if (location is not null)
                {
                    location.Checkpoint.State = result.State;
                    location.Checkpoint.PendingResult = null;
                    WorkerCheckpointStore<TranscodeAssignment, TranscodeCheckpointPayload>.Save(location);
                }
                return;
            }
            catch (WorkerLeaseLostException error)
            {
                if (location is not null)
                {
                    location.Checkpoint.State = "Rejected";
                    WorkerCheckpointStore<TranscodeAssignment, TranscodeCheckpointPayload>.Save(location);
                }
                reporter.Message("The pending result lost its execution lease: " + error.Message, "Warning");
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
            {
                reporter.Message("The final state remains pending; control heartbeats will continue: " + error.Message, "Warning");
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
                job.PulseStages?.Invoke();
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
