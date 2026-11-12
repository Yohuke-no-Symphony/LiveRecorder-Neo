namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>单房间运行任务及其取消生命周期。</summary>
public sealed record RecordingWorkerHandle(CancellationTokenSource Cancellation, Task Task);
