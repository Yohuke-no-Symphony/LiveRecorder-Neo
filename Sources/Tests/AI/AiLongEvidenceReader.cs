using System.Globalization;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Tests;

internal sealed class AiLongEvidenceReader(IEnumerable<AiEvidence> items) : IAiEvidenceReader
{
    internal List<AiEvidence> Items { get; } = items.ToList();
    internal List<(double FromMs, double ToMs, string? Cursor)> Requests { get; } = [];

    public Task<AiEvidencePage> ReadAsync(AiAnalysisSnapshot snapshot, double fromMs, double toMs, string? kind,
        string? query, string? cursor, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add((fromMs, toMs, cursor));
        var skip = cursor is null ? 0 : int.Parse(cursor, CultureInfo.InvariantCulture);
        var matches = Items.Select((item, index) => (Item: item, Index: index)).Where(pair => pair.Index >= skip &&
            pair.Item.OffsetMs >= fromMs && pair.Item.OffsetMs < toMs &&
            (kind is null or "All" || pair.Item.Kind == kind) &&
            (query is null || pair.Item.Text.Contains(query, StringComparison.OrdinalIgnoreCase))).Take(limit + 1).ToArray();
        var page = matches.Take(limit).ToArray();
        return Task.FromResult(new AiEvidencePage
        {
            Items = page.Select(pair => pair.Item).ToList(),
            Truncated = matches.Length > limit,
            NextCursor = matches.Length > limit ? (page[^1].Index + 1).ToString(CultureInfo.InvariantCulture) : null
        });
    }
}
