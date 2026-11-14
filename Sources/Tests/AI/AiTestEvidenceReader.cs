using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Tests;

internal sealed class AiTestEvidenceReader : IAiEvidenceReader
{
    internal List<AiEvidence> Evidence { get; } = [new() { Id = "0:1", OffsetMs = 1000, Kind = "Translation", Text = "【正在介绍下一首歌曲】" }];

    public Task<AiEvidencePage> ReadAsync(AiAnalysisSnapshot snapshot, double fromMs, double toMs, string? kind, string? query, string? cursor, int limit, CancellationToken cancellationToken) =>
        Task.FromResult(new AiEvidencePage { Items = Evidence.Where(item => item.OffsetMs >= fromMs && item.OffsetMs < toMs &&
            (kind is null or "All" || item.Kind == kind) && (query is null || item.Text.Contains(query))).Take(limit).ToList() });
}
