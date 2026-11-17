using System.Text.Json;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.AI;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.AI.Tests;

internal sealed class AiNodeTestCheckpoint(AiJobExecutionService execution, string workerId, AiAssignment assignment,
    AiExecutionSpecification specification) : IAiRunCheckpoint
{
    private long sequence = specification.CheckpointSequence;
    private AiAnalysisMemory? memory = specification.Memory;

    /// <inheritdoc />
    public Task SaveAsync(AiAnalysisJob job, CancellationToken cancellationToken) => SaveAsync(job, null, cancellationToken);

    /// <inheritdoc />
    public async Task SaveMemoryAsync(AiAnalysisJob job, AiAnalysisMemory value, CancellationToken cancellationToken)
    {
        var previous = job.Context.ConversationVersion;
        value.JobId = job.Id;
        value.SessionId = job.SessionId;
        value.Version = previous + 1;
        job.Context.ConversationVersion = value.Version;
        try
        {
            await SaveAsync(job, value, cancellationToken);
            memory = Clone(value);
        }
        catch
        {
            job.Context.ConversationVersion = previous;
            throw;
        }
    }

    /// <inheritdoc />
    public Task<AiAnalysisMemory> LoadMemoryAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(memory is null ? new() : Clone(memory));
    }

    internal async Task CompleteAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        try
        {
            var acknowledged = await execution.CompleteAsync(workerId, job.Id, assignment.Attempt, assignment.LeaseToken,
                new() { Sequence = sequence + 1, Execution = AiExecutionMapper.ToExecution(job), Memory = memory }, cancellationToken);
            sequence = acknowledged.Sequence;
        }
        catch (InfrastructureException error) when (error.StatusCode is 409 or 410)
        {
            throw new AiExecutionStoppedException(error.Code ?? "AI_LEASE_LOST");
        }
    }

    private async Task SaveAsync(AiAnalysisJob job, AiAnalysisMemory? value, CancellationToken cancellationToken)
    {
        try
        {
            var acknowledged = await execution.CheckpointAsync(workerId, job.Id, assignment.Attempt, assignment.LeaseToken,
                new() { Sequence = sequence + 1, Execution = AiExecutionMapper.ToExecution(job), Memory = value }, cancellationToken);
            sequence = acknowledged.Sequence;
        }
        catch (InfrastructureException error) when (error.StatusCode is 409 or 410)
        {
            throw new AiExecutionStoppedException(error.Code ?? "AI_LEASE_LOST");
        }
    }

    private static AiAnalysisMemory Clone(AiAnalysisMemory value) =>
        JsonSerializer.Deserialize<AiAnalysisMemory>(JsonSerializer.SerializeToUtf8Bytes(value, WorkerProtocol.JsonOptions), WorkerProtocol.JsonOptions)!;
}
