using Yohuke.LiveRecorder.Worker.Common.Stages;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

internal sealed class TestWorkerStage(string id, Func<StageTestContext, CancellationToken, Task<WorkerStageResult>>? execute = null)
    : IWorkerStage<StageTestContext>
{
    public WorkerStageDefinition Definition { get; } = new(id, 0, 100);

    /// <inheritdoc />
    public Task<bool> RestoreAsync(StageTestContext context, CancellationToken cancellationToken) => Task.FromResult(context.ValidArtifacts.Contains(id));

    /// <inheritdoc />
    public async Task<WorkerStageResult> ExecuteAsync(StageTestContext context, CancellationToken cancellationToken)
    {
        context.Executions[id] = context.Executions.GetValueOrDefault(id) + 1;
        if (execute is not null)
        {
            return await execute(context, cancellationToken);
        }
        context.ValidArtifacts.Add(id);
        return WorkerStageResult.Completed;
    }
}
