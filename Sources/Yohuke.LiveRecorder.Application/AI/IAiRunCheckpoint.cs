using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application.AI;

/// <summary>在模型请求和工具提交之间持久化租约约束的检查点。</summary>
public interface IAiRunCheckpoint
{
    /// <summary>校验任务未取消并原子保存当前状态。</summary>
    Task SaveAsync(AiAnalysisJob job, CancellationToken cancellationToken);

    /// <summary>原子发布私有上下文文件，再持久化其版本与结构化进度。</summary>
    Task SaveMemoryAsync(AiAnalysisJob job, AiAnalysisMemory memory, CancellationToken cancellationToken) => SaveAsync(job, cancellationToken);

    /// <summary>读取与持久版本匹配的上下文；无历史时返回空记忆。</summary>
    Task<AiAnalysisMemory> LoadMemoryAsync(AiAnalysisJob job, CancellationToken cancellationToken) => Task.FromResult(new AiAnalysisMemory());
}
