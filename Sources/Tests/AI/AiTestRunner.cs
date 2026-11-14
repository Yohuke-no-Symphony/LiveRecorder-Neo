using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Tests;

internal sealed class AiTestRunner(Func<AiAnalysisJob, IAiRunCheckpoint, CancellationToken, Task> run) : IAiAnalysisRunner
{
    public Task RunAsync(AiAnalysisJob job, string apiKey, IAiRunCheckpoint checkpoint, CancellationToken cancellationToken) => run(job, checkpoint, cancellationToken);
}
