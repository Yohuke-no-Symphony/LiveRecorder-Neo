using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application.AI;

/// <summary>执行与 HTTP、数据库和界面无关的复盘分析。</summary>
public interface IAiAnalysisRunner
{
    /// <summary>在已领取作业上运行有界工具循环，明文密钥仅存活于本次执行。</summary>
    Task RunAsync(AiAnalysisJob job, string apiKey, IAiRunCheckpoint checkpoint, CancellationToken cancellationToken);
}
