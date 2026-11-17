using Microsoft.Extensions.Hosting;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.AI.Tests;

internal sealed class AiNodeTestHost(AiMongoFixture fixture, IAiAnalysisRunner runner) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var worker = await fixture.RegisterNodeAsync(stoppingToken);
        Task? running = null;
        AiAssignment? assignment = null;
        CancellationTokenSource? runCancellation = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (running?.IsCompleted == true)
                {
                    await running;
                    running = null;
                    assignment = null;
                    runCancellation?.Dispose();
                    runCancellation = null;
                }
                var heartbeat = new WorkerHeartbeat
                {
                    AppliedConfigurationRevision = worker.ConfigurationRevision,
                    Capabilities = new() { AiExecutionVersion = 1 }, Telemetry = new() { FreeBytes = long.MaxValue }
                };
                if (assignment is not null)
                {
                    heartbeat.Jobs.Add(new()
                    {
                        JobId = assignment.JobId, Attempt = assignment.Attempt, LeaseToken = assignment.LeaseToken
                    });
                }
                var state = await fixture.Scheduler.HeartbeatAsync(worker.Id, heartbeat, stoppingToken);
                if (running is not null && (running.IsCompleted || state.Leases.Any(lease => !lease.Accepted)))
                {
                    if (!running.IsCompleted)
                    {
                        await runCancellation!.CancelAsync();
                    }
                    await running;
                    running = null;
                    assignment = null;
                    runCancellation?.Dispose();
                    runCancellation = null;
                }
                if (running is null && state.AiAssignments.SingleOrDefault() is { } next)
                {
                    assignment = next;
                    runCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    running = RunAsync(worker.Id, next, runCancellation.Token);
                }
                await Task.Delay(20, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (runCancellation is not null)
            {
                await runCancellation.CancelAsync();
            }
            if (running is not null)
            {
                await running;
            }
            runCancellation?.Dispose();
        }
    }

    private async Task RunAsync(string workerId, AiAssignment assignment, CancellationToken cancellationToken)
    {
        AiAnalysisJob? job = null;
        AiNodeTestCheckpoint? checkpoint = null;
        try
        {
            var specification = await fixture.Execution.GetExecutionAsync(workerId, assignment.JobId, assignment.Attempt,
                assignment.LeaseToken, cancellationToken);
            job = AiExecutionMapper.ToJob(specification.Execution);
            job.Input = specification.Input;
            var sources = new Dictionary<string, string>();
            foreach (var source in specification.Input.Sources)
            {
                var asset = await fixture.Execution.GetSourceAsync(workerId, assignment.JobId, assignment.Attempt,
                    assignment.LeaseToken, source.Id, cancellationToken);
                sources.Add(source.Id, asset.Path);
            }
            await fixture.PrepareSnapshotAsync(job, sources, cancellationToken);
            checkpoint = new(fixture.Execution, workerId, assignment, specification);
            await runner.RunAsync(job, specification.ApiKey, checkpoint, cancellationToken);
        }
        catch (InfrastructureException error) when (error.StatusCode is 409 or 410)
        {
            return;
        }
        catch (OperationCanceledException error)
        {
            if (job is null)
            {
                return;
            }
            job.State = "Interrupted";
            job.ErrorCode = error is AiExecutionStoppedException stopped ? stopped.Code : "AI_PROCESS_INTERRUPTED";
        }
        catch (Exception)
        {
            if (job is null)
            {
                throw;
            }
            job.State = job.RequestInFlight ? "Interrupted" : "Failed";
            job.ErrorCode = job.RequestInFlight ? "AI_REQUEST_UNKNOWN" : "AI_EXECUTION_FAILED";
        }
        if (job is null || checkpoint is null)
        {
            return;
        }
        if (job.State == "Running")
        {
            job.State = "Partial";
        }
        try
        {
            await checkpoint.CompleteAsync(job, CancellationToken.None);
        }
        catch (AiExecutionStoppedException)
        {
        }
    }
}
