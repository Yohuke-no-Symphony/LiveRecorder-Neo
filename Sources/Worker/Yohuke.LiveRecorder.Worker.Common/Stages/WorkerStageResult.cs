namespace Yohuke.LiveRecorder.Worker.Common.Stages;

internal sealed record WorkerStageResult(string? RestartFromStageId = null)
{
    internal static WorkerStageResult Completed { get; } = new();
}
