using System.Globalization;
using System.Text.Json;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI;

/// <summary>只从节点工作区内的固定精简文件查询模型证据。</summary>
public sealed class AiFileEvidenceReader(string directory) : IAiEvidenceReader
{
    private const int MAX_PAGE_CHARACTERS = 4000;
    private readonly string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<AiEvidencePage> ReadAsync(AiAnalysisSnapshot snapshot, double fromMs, double toMs, string? kind,
        string? query, string? cursor, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 40 || !double.IsFinite(fromMs) || !double.IsFinite(toMs) || fromMs < 0
            || toMs > snapshot.DurationMs || fromMs > toMs)
        {
            throw new InvalidDataException("Invalid evidence range.");
        }
        var skip = 0L;
        if (cursor is not null && (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out skip) || skip < 0))
        {
            throw new InvalidDataException("Invalid evidence cursor.");
        }
        var path = Path.GetFullPath(Path.Combine(directory, snapshot.EvidenceRelativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Path.IsPathFullyQualified(snapshot.EvidenceRelativePath)
            || !path.StartsWith(directory + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidDataException("The evidence file is outside the AI workspace.");
        }
        var page = new AiEvidencePage();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var characterCount = 0;
        var row = 0L;
        using var input = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true));
        while (await input.ReadLineAsync(cancellationToken) is { } line)
        {
            row++;
            if (row <= skip)
            {
                continue;
            }
            var item = JsonSerializer.Deserialize<AiEvidence>(line, JsonOptions)
                ?? throw new InvalidDataException("The evidence file contains an empty row.");
            if (item.OffsetMs < fromMs || item.OffsetMs >= toMs || kind is not (null or "All") && item.Kind != kind
                || query is not null && !item.Text.Contains(query, StringComparison.OrdinalIgnoreCase) || !seen.Add(item.Kind + "\n" + item.Text))
            {
                continue;
            }
            if (page.Items.Count >= limit || characterCount + item.Text.Length > MAX_PAGE_CHARACTERS)
            {
                page.Truncated = true;
                page.NextCursor = (row - 1).ToString(CultureInfo.InvariantCulture);
                break;
            }
            page.Items.Add(item);
            characterCount += item.Text.Length;
        }
        return page;
    }
}
