using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Tests;

internal sealed class AiRetryRejectingCheckpoint(bool rejectFailureSave = false) : IAiRunCheckpoint
{
    internal AiTestCheckpoint Inner { get; } = new();

    public Task SaveAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        if (job.RequestInFlight && job.ModelRequests > 1)
        {
            throw new AiExecutionStoppedException("AI_CONFIGURATION_CHANGED");
        }
        return Inner.SaveAsync(job, cancellationToken);
    }

    public Task SaveMemoryAsync(AiAnalysisJob job, AiAnalysisMemory memory, CancellationToken cancellationToken)
    {
        if (rejectFailureSave && job.ErrorCode == "AI_RESPONSE_INTERRUPTED")
        {
            throw new IOException("Simulated checkpoint persistence failure.");
        }
        return Inner.SaveMemoryAsync(job, memory, cancellationToken);
    }
}
