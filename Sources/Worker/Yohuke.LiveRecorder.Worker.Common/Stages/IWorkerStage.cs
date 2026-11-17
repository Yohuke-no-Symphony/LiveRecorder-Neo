namespace Yohuke.LiveRecorder.Worker.Common.Stages;

internal interface IWorkerStage<in TContext> where TContext : WorkerStageContext
{
    WorkerStageDefinition Definition { get; }
    Task<bool> RestoreAsync(TContext context, CancellationToken cancellationToken);
    Task<WorkerStageResult> ExecuteAsync(TContext context, CancellationToken cancellationToken);
}
