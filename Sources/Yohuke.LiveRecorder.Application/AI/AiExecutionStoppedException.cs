namespace Yohuke.LiveRecorder.Application.AI;

/// <summary>任务授权或执行条件改变导致的可说明停止。</summary>
public sealed class AiExecutionStoppedException(string code) : OperationCanceledException(code)
{
    public string Code { get; } = code;
}
