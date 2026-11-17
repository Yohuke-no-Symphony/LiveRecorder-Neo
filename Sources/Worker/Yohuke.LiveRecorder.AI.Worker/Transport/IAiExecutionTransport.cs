using Yohuke.LiveRecorder.AI.Contracts;

namespace Yohuke.LiveRecorder.AI.Worker.Transport;

internal interface IAiExecutionTransport
{
    Task<AiExecutionSpecification> GetExecutionAsync(AiAssignment assignment, CancellationToken cancellationToken);
    Task DownloadAsync(AiAssignment assignment, AiInputSource source, string destination, long bytesPerSecond, CancellationToken cancellationToken);
    Task<AiCheckpointAcknowledgement> CheckpointAsync(AiAssignment assignment, AiExecutionCheckpoint payload, CancellationToken cancellationToken);
    Task<AiCheckpointAcknowledgement> CompleteAsync(AiAssignment assignment, AiExecutionResult payload, CancellationToken cancellationToken);
}
