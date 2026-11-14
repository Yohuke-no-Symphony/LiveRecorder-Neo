using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Tests;

internal sealed class AiPreflightRejectingCheckpoint : IAiRunCheckpoint
{
    public Task SaveAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        if (job.RequestInFlight)
        {
            throw new AiExecutionStoppedException("AI_CANCELLED");
        }
        return Task.CompletedTask;
    }
}
