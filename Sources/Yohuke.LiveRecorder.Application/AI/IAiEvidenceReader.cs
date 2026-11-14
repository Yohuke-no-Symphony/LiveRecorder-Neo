using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application.AI;

/// <summary>只查询当前分析快照中的精简证据。</summary>
public interface IAiEvidenceReader
{
    /// <summary>在固定时间窗口内按字面关键词和正文类型查询。</summary>
    Task<AiEvidencePage> ReadAsync(AiAnalysisSnapshot snapshot, double fromMs, double toMs, string? kind,
        string? query, string? cursor, int limit, CancellationToken cancellationToken);
}
