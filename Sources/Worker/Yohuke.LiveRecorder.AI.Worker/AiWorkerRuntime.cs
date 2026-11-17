using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.AI.Worker.Execution;
using Yohuke.LiveRecorder.AI.Worker.Storage;
using Yohuke.LiveRecorder.AI.Worker.Transport;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.AI.Worker;

internal sealed class AiWorkerRuntime(WorkerConnectionOptions options, WorkerSettingsStore settings, Action<string>? log = null,
    Func<HttpMessageHandler>? apiHandlerFactory = null, Func<HttpMessageHandler>? providerHandlerFactory = null)
{
    private readonly Action<string> log = log ?? Console.WriteLine;
    private readonly ConcurrentDictionary<string, AiRunningJob> jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiCheckpointStore> recoveries = new(StringComparer.Ordinal);
    private readonly WorkerLogBuffer logs = new();
    private AiWorkerConfiguration configuration = new();
    private int appliedRevision;
    private string? configurationError;
    private readonly WorkerCapabilities capabilities = new()
    {
        AiExecutionVersion = 1, OperatingSystem = RuntimeInformation.OSDescription,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(), ToolVersion = typeof(AiWorkerRuntime).Assembly.GetName().Version?.ToString() ?? "1"
    };

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workspace = new AiWorkspace(settings.Path, options.WorkerId, message => WriteLog(message, "Warning"));
        using var transfer = new AiExecutionTransport(options, apiHandlerFactory?.Invoke(), () => configuration.UploadBytesPerSecond);
        if (Path.IsPathFullyQualified(settings.Current.Configuration.TempDirectory))
        {
            workspace.RegisterRoot(settings.Current.Configuration.TempDirectory);
        }
        foreach (var store in workspace.ReadPending().OrderByDescending(value => value.Read().LastAccessAtUtc))
        {
            recoveries.TryAdd(store.Read().Assignment.JobId, store);
        }
        var delay = 1;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await using var control = await WorkerControlClient.ConnectAsync(options, lifetime.Token);
                    var welcome = await control.ExchangeAsync<WorkerHello, WorkerWelcome>("Hello", "Welcome", new()
                    {
                        Kind = "AI", WorkerId = options.WorkerId, Secret = options.Secret, Capabilities = capabilities
                    }, lifetime.Token);
                    await ApplyConfigurationAsync(welcome.Configuration, welcome.ConfigurationRevision, workspace, lifetime.Token);
                    delay = 1;
                    WriteLog("Connected to the AI coordinator.");
                    while (!lifetime.IsCancellationRequested)
                    {
                        CollectFinished();
                        var requested = welcome.RecoveryProtocolVersion >= 1 ? RecoveryRequests() : [];
                        var state = await control.ExchangeAsync<WorkerHeartbeat, WorkerState>("Heartbeat", "State", new()
                        {
                            AppliedConfigurationRevision = appliedRevision, ConfigurationError = configurationError,
                            Capabilities = configurationError is null ? capabilities : capabilities with { AiExecutionVersion = 0 },
                            Telemetry = new() { FreeBytes = FreeBytes(), RunningCpu = jobs.Count }, Jobs = jobs.Values.Select(job => job.Snapshot()).ToList(),
                            RecoveryRequests = requested, AiRecoveries = requested.Select(item => RecoveryCheckpoint(recoveries[item.JobId])).ToList(),
                            LogBatch = welcome.LogProtocolVersion >= 1 ? logs.GetBatch() : null
                        }, lifetime.Token);
                        logs.Acknowledge(state.LogAcknowledgement);
                        await ApplyConfigurationAsync(state.Configuration, state.ConfigurationRevision, workspace, lifetime.Token);
                        foreach (var lease in state.Leases)
                        {
                            if (jobs.TryGetValue(lease.JobId, out var job) && job.Store.Read().Assignment.Attempt == lease.Attempt)
                            {
                                if (lease.Accepted)
                                {
                                    job.Lease.Renew(lease.LeaseSeconds);
                                }
                                else
                                {
                                    job.Lease.Disconnect();
                                    await job.Cancellation.CancelAsync();
                                }
                            }
                        }
                        foreach (var recovery in state.RecoveryResults)
                        {
                            if (!recoveries.TryGetValue(recovery.JobId, out var store) || store.Read().Assignment.Attempt != recovery.Attempt)
                            {
                                continue;
                            }
                            if (recovery.Disposition is "Resume" or "ReportOnly")
                            {
                                store.Update(value =>
                                {
                                    value.Assignment.LeaseToken = value.NextLeaseToken!;
                                    value.NextLeaseToken = null;
                                });
                                recoveries.Remove(recovery.JobId);
                                Start(store, recovery.LeaseSeconds, recovery.Disposition == "ReportOnly", true, workspace, transfer, lifetime.Token);
                            }
                            else
                            {
                                store.Update(value =>
                                {
                                    value.State = recovery.Disposition == "Completed" ? "Completed" : "Rejected";
                                    if (value.State == "Completed")
                                    {
                                        value.Pending = null;
                                        value.PendingResult = null;
                                    }
                                });
                                recoveries.Remove(recovery.JobId);
                                WriteLog(recovery.Disposition == "Completed" ? "The coordinator confirmed the AI job had already finished."
                                    : "AI recovery was rejected; no model request was repeated.", "Warning", recovery.JobId, recovery.Attempt);
                            }
                        }
                        foreach (var assignment in state.AiAssignments)
                        {
                            CollectFinished();
                            if (jobs.TryGetValue(assignment.JobId, out var previous))
                            {
                                if (previous.Store.Read().Assignment.Attempt != assignment.Attempt)
                                {
                                    var replacement = workspace.Open(configuration, assignment);
                                    replacement.Update(value => value.State = "Paused");
                                    recoveries[assignment.JobId] = replacement;
                                    previous.Lease.Disconnect();
                                    await previous.Cancellation.CancelAsync();
                                }
                                continue;
                            }
                            if (recoveries.TryGetValue(assignment.JobId, out var existing))
                            {
                                if (existing.Read().Assignment.Attempt == assignment.Attempt)
                                {
                                    continue;
                                }
                                existing.Update(value => value.State = "Rejected");
                                recoveries.Remove(assignment.JobId);
                            }
                            var store = workspace.Open(configuration, assignment);
                            if (store.Read().State == "Completed")
                            {
                                continue;
                            }
                            if (configurationError is not null || jobs.Count >= configuration.MaxConcurrentJobs)
                            {
                                store.Update(value => value.State = "Paused");
                                recoveries[assignment.JobId] = store;
                                continue;
                            }
                            Start(store, assignment.LeaseSeconds, false, false, workspace, transfer, lifetime.Token);
                        }
                        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(welcome.HeartbeatSeconds, 1, 10)), lifetime.Token);
                    }
                }
                catch (WorkerAuthenticationException)
                {
                    throw;
                }
                catch (Exception error) when (!lifetime.IsCancellationRequested && error is IOException or SocketException or AuthenticationException or OperationCanceledException)
                {
                    foreach (var job in jobs.Values)
                    {
                        job.Lease.Disconnect();
                    }
                    WriteLog("AI control connection interrupted (" + error.GetType().Name + "). Reconnecting.", "Warning");
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
                job.Lease.Disconnect();
                await job.Cancellation.CancelAsync();
            }
            await Task.WhenAll(jobs.Values.Select(job => job.Task));
            foreach (var job in jobs.Values)
            {
                job.Cancellation.Dispose();
            }
        }
    }

    private async Task ApplyConfigurationAsync(WorkerConfiguration value, int revision, AiWorkspace workspace, CancellationToken cancellationToken)
    {
        await settings.SaveRemoteConfigurationAsync(value, revision, cancellationToken);
        try
        {
            WorkerConfigurationValidation.Validate(value, "AI");
            var updated = WorkerConfigurationProjection.ToAi(value);
            workspace.RegisterRoot(updated.TempDirectory);
            workspace.EnsureSpace(updated, 0);
            configuration = updated;
            appliedRevision = revision;
            configurationError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            configurationError = "AI workspace configuration is unavailable (" + error.GetType().Name + ").";
        }
    }

    private void CollectFinished()
    {
        foreach (var pair in jobs.ToArray())
        {
            if (pair.Value.Task.IsCompleted && jobs.TryRemove(pair.Key, out var job))
            {
                if (job.Store.Read().State is not ("Completed" or "Rejected"))
                {
                    if (!recoveries.TryGetValue(pair.Key, out var replacement) || replacement.Read().Assignment.Attempt <= job.Store.Read().Assignment.Attempt)
                    {
                        recoveries[pair.Key] = job.Store;
                    }
                }
                if (job.Task.IsFaulted)
                {
                    _ = job.Task.Exception;
                    WriteLog("AI task stopped before it could persist its final checkpoint.", "Error", pair.Key, job.Store.Read().Assignment.Attempt);
                }
                job.Cancellation.Dispose();
            }
        }
    }

    private List<WorkerJobRecoveryRequest> RecoveryRequests()
    {
        if (configurationError is not null)
        {
            return [];
        }
        var slots = Math.Max(0, configuration.MaxConcurrentJobs - jobs.Count);
        return recoveries.Values.Take(slots).Select(store =>
        {
            if (store.Read().NextLeaseToken is null)
            {
                store.Update(value => value.NextLeaseToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
            }
            var current = store.Read();
            return new WorkerJobRecoveryRequest
            {
                JobId = current.Assignment.JobId, Attempt = current.Assignment.Attempt,
                LeaseToken = current.Assignment.LeaseToken, NextLeaseToken = current.NextLeaseToken!
            };
        }).ToList();
    }

    private static AiRecoveryCheckpoint RecoveryCheckpoint(AiCheckpointStore store)
    {
        return store.RecoveryCheckpoint();
    }

    private void Start(AiCheckpointStore store, int leaseSeconds, bool reportOnly, bool recovering,
        AiWorkspace workspace, IAiExecutionTransport transfer, CancellationToken lifetime)
    {
        var job = new AiRunningJob(store, lifetime);
        job.Lease.Renew(leaseSeconds, reportOnly);
        store.Update(value => value.State = "Working");
        jobs.AddOrUpdate(store.Read().Assignment.JobId, job, (_, _) => throw new InvalidOperationException("An AI job is already running."));
        var limits = configuration;
        job.Task = ExecuteAsync(job, limits, workspace, transfer, recovering);
    }

    private async Task ExecuteAsync(AiRunningJob running, AiWorkerConfiguration limits, AiWorkspace workspace,
        IAiExecutionTransport transfer, bool recovering)
    {
        var store = running.Store;
        var checkpoint = new AiRemoteCheckpoint(store, running.Lease, transfer);
        var token = running.Cancellation.Token;
        var assignment = store.Read().Assignment;
        AiAnalysisJob? job = null;
        try
        {
            var specification = await transfer.GetExecutionAsync(assignment, token);
            if (specification.Execution.JobId != assignment.JobId || specification.IsConnectionTest != assignment.IsConnectionTest ||
                !assignment.IsConnectionTest && specification.Input.Fingerprint != assignment.InputFingerprint)
            {
                throw new InvalidDataException("The AI execution specification has a different identity.");
            }
            if (!recovering || store.Read().Execution.SessionId.Length == 0 && store.Read().Execution.ModelRequests == 0)
            {
                store.Update(value =>
                {
                    value.Execution = specification.Execution;
                    value.Memory = specification.Memory ?? new()
                    {
                        JobId = specification.Execution.JobId,
                        SessionId = specification.Execution.SessionId
                    };
                    value.Input = specification.Input;
                    value.Sequence = specification.CheckpointSequence;
                });
            }
            if (store.Read().PendingResult is not null)
            {
                await checkpoint.CompleteAsync(AiExecutionMapper.ToJob(store.Read().PendingResult!.Execution),
                    store.Read().PendingResult!.ConnectionTest, token);
                return;
            }
            var directory = Path.GetDirectoryName(store.Path)!;
            if (!assignment.IsConnectionTest && (!store.Read().InputReady || !File.Exists(Path.Combine(directory, "evidence.jsonl"))))
            {
                var requiredBytes = checked(specification.Input.Sources.Sum(source => source.Length) * 2);
                workspace.EnsureSpace(limits, requiredBytes);
                var sources = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var source in specification.Input.Sources)
                {
                    var destination = AiWorkspace.SourcePath(directory, source.Id);
                    await transfer.DownloadAsync(assignment, source, destination, limits.DownloadBytesPerSecond, token);
                    sources.Add(source.Id, destination);
                }
                running.Stage = "Preparing";
                var snapshot = await new AiSnapshotBuilder().BuildAsync(specification.Execution, specification.Input, sources, directory, token);
                store.Update(value =>
                {
                    value.Execution.Snapshot = snapshot;
                    value.InputReady = true;
                });
            }
            job = AiExecutionMapper.ToJob(store.Read().Execution);
            if (job.State is "Completed" or "Partial" or "Failed" or "Interrupted" or "Cancelled")
            {
                await checkpoint.CompleteAsync(job, null, token);
                return;
            }
            if (store.Read().Journal is { Dispatched: true, HasKnownResponse: false })
            {
                job.State = "Interrupted";
                job.ErrorCode = "AI_REQUEST_UNKNOWN";
                await checkpoint.CompleteAsync(job, null, token);
                return;
            }
            if (recovering)
            {
                job = checkpoint.PrepareRecovery(specification);
            }
            using var http = new HttpClient(providerHandlerFactory?.Invoke()
                ?? new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    ConnectTimeout = TimeSpan.FromSeconds(15)
                });
            http.Timeout = Timeout.InfiniteTimeSpan;
            var client = new JournaledAiChatClient(new OpenAiCompatibleChatClient(http), store, running.Lease,
                () => workspace.EnsureSpace(limits, 0));
            running.Stage = "Analyzing";
            if (assignment.IsConnectionTest)
            {
                var outcome = await RunConnectionTestAsync(job, specification.ApiKey, client, checkpoint, token);
                running.FinishStage(outcome.Success ? "Completed" : "Failed", job.ErrorCode);
                running.Stage = "Reporting";
                running.RefreshStages();
                await checkpoint.CompleteAsync(job, outcome, token);
                return;
            }
            await new AiAnalysisRunner(client, new AiFileEvidenceReader(directory)).RunAsync(job, specification.ApiKey, checkpoint, token);
            running.FinishStage(job.State == "Completed" ? "Completed" : job.State == "Interrupted" ? "Interrupted" : "Failed", job.ErrorCode);
            running.Stage = "Reporting";
            running.RefreshStages();
            await checkpoint.CompleteAsync(job, null, token);
        }
        catch (AiExecutionPausedException)
        {
            if (store.Read().State == "Rejected")
            {
                WriteLog("The retained AI response did not match the restored request; no model call was repeated.", "Error", assignment.JobId, assignment.Attempt);
                return;
            }
            if (running.Lease.ReportOnly && job is not null && !job.RequestInFlight)
            {
                job.State = job.Result.Topics.Count + job.Result.LiveMarkers.Count > 0 ? "Partial" : "Interrupted";
                job.ErrorCode = "AI_PROCESS_INTERRUPTED";
                running.FinishStage("Interrupted", job.ErrorCode);
                running.Stage = "Reporting";
                running.RefreshStages();
                try
                {
                    await checkpoint.CompleteAsync(job, null, token);
                }
                catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
                {
                    store.Update(value => value.State = "Paused");
                }
            }
            else
            {
                store.Update(value => value.State = "Paused");
            }
        }
        catch (WorkerLeaseLostException)
        {
            store.Update(value => value.State = "Paused");
            WriteLog("AI lease no longer authorizes writes; the local response was retained.", "Warning", assignment.JobId, assignment.Attempt);
        }
        catch (OperationCanceledException)
        {
            store.Update(value => value.State = "Paused");
        }
        catch (Exception error) when (error is InvalidDataException or InfrastructureException or ArgumentException or OverflowException)
        {
            job ??= AiExecutionMapper.ToJob(store.Read().Execution);
            job.State = "Failed";
            job.ErrorCode = error is InfrastructureException business ? business.Code ?? "AI_EXECUTION_FAILED" : "AI_SOURCE_UNAVAILABLE";
            running.FinishStage("Failed", job.ErrorCode);
            running.Stage = "Reporting";
            running.RefreshStages();
            try
            {
                await checkpoint.CompleteAsync(job, assignment.IsConnectionTest ? new() { Success = false } : null, token);
            }
            catch (Exception failure) when (failure is IOException or HttpRequestException or OperationCanceledException)
            {
                store.Update(value => value.State = "Paused");
            }
            WriteLog("AI execution failed (" + error.GetType().Name + ").", "Warning", assignment.JobId, assignment.Attempt);
        }
        catch (Exception error)
        {
            store.Update(value => value.State = "Paused");
            WriteLog("AI execution paused (" + error.GetType().Name + "). Its checkpoint was retained.", "Warning", assignment.JobId, assignment.Attempt);
        }
    }

    private static async Task<AiConnectionTestOutcome> RunConnectionTestAsync(AiAnalysisJob job, string apiKey, IAiChatClient client,
        AiRemoteCheckpoint checkpoint, CancellationToken token)
    {
        job.ModelRequests++;
        job.RequestInFlight = true;
        try
        {
            await checkpoint.SaveAsync(job, token);
        }
        catch
        {
            job.ModelRequests--;
            job.RequestInFlight = false;
            throw;
        }
        AiConnectionTestOutcome outcome;
        try
        {
            var result = await new AiConnectionTester(client).TestAsync(job.Settings, apiKey, token);
            outcome = new() { Success = result.Success, PromptTokens = result.PromptTokens, CompletionTokens = result.CompletionTokens };
            job.PromptTokens += result.PromptTokens ?? 0;
            job.CompletionTokens += result.CompletionTokens ?? 0;
            job.State = "Completed";
        }
        catch (InfrastructureException error)
        {
            outcome = new() { Success = false };
            job.State = "Failed";
            job.ErrorCode = error.Code ?? "AI_REQUEST_FAILED";
        }
        job.RequestInFlight = false;
        await checkpoint.SaveMemoryAsync(job, new(), token);
        return outcome;
    }

    private long FreeBytes()
    {
        try
        {
            return string.IsNullOrEmpty(configuration.TempDirectory) ? 0 : AiWorkspace.FreeBytes(configuration.TempDirectory);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private void WriteLog(string message, string level = "Information", string? jobId = null, int? attempt = null)
    {
        log(message);
        logs.Append(new() { Message = message, Level = level, JobId = jobId, Attempt = attempt, Source = "Worker" });
    }
}
