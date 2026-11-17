using System.Text.Json;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.AI.Worker.Transport;

namespace Yohuke.LiveRecorder.AI.Worker.Tests;

internal sealed class AiTestTransport : IAiExecutionTransport
{
    internal int FailCheckpointCount { get; set; }
    internal Func<AiExecutionCheckpoint, Task>? OnCheckpoint { get; set; }
    internal int FailResultCount { get; set; }
    internal List<AiExecutionCheckpoint> Checkpoints { get; } = [];
    internal List<AiExecutionResult> Results { get; } = [];

    public Task<AiExecutionSpecification> GetExecutionAsync(AiAssignment assignment, CancellationToken cancellationToken) =>
        Task.FromResult(new AiExecutionSpecification());

    public Task DownloadAsync(AiAssignment assignment, AiInputSource source, string destination, long bytesPerSecond, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<AiCheckpointAcknowledgement> CheckpointAsync(AiAssignment assignment, AiExecutionCheckpoint payload, CancellationToken cancellationToken)
    {
        Checkpoints.Add(JsonSerializer.Deserialize<AiExecutionCheckpoint>(JsonSerializer.Serialize(payload))!);
        if (FailCheckpointCount-- > 0)
        {
            throw new IOException("Acknowledgement lost.");
        }
        if (OnCheckpoint is not null)
        {
            await OnCheckpoint(payload);
        }
        return new() { Sequence = payload.Sequence, State = payload.Execution.State };
    }

    public Task<AiCheckpointAcknowledgement> CompleteAsync(AiAssignment assignment, AiExecutionResult payload, CancellationToken cancellationToken)
    {
        Results.Add(payload);
        if (FailResultCount-- > 0)
        {
            throw new IOException("Result acknowledgement lost.");
        }
        return Task.FromResult(new AiCheckpointAcknowledgement { Sequence = payload.Sequence, State = payload.Execution.State });
    }
}
