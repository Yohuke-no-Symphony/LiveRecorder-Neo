using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Analytics;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Infrastructure.AI;

/// <summary>把固定播放范围投影为不包含原始协议或用户资料的分析快照。</summary>
public sealed class AiArchiveDataSource(EventArchiveReader reader, IAppPaths paths, AnalyticsService analytics) : IAiEvidenceReader
{
    private const int MAX_PAGE_CHARACTERS = 4000;

    /// <summary>计算数据、播放范围和实际同传规则的身份，不创建缓存。</summary>
    public string Fingerprint(SessionDetail detail, ServerSettings settings)
    {
        var sources = SessionGroupingService.Sources(detail.Session, detail.DurationMs);
        return Hash(JsonSerializer.Serialize(new
        {
            version = AiAnalysisPolicy.ANALYSIS_VERSION,
            detail.Session.Id, detail.Session.StartedAtUtc, detail.Session.MediaRevision, detail.Session.ConfigurationRevision, detail.DurationMs,
            pattern = TranslationDanmuPattern.Resolve(detail.Session, settings),
            sources = sources.Select(source =>
            {
                var file = new FileInfo(ArchivePath(source.DirectoryPath));
                return new { source.DirectoryPath, source.FromMs, source.ToMs, source.StartOffsetMs,
                    length = file.Exists ? file.Length : 0, modified = file.Exists ? file.LastWriteTimeUtc.Ticks : 0 };
            })
        }));
    }

    /// <summary>一次读取所有来源的已提交快照，保存精简正文以隔离后续归档变化。</summary>
    public async Task<AiAnalysisSnapshot> CreateAsync(SessionDetail detail, ServerSettings settings, CancellationToken cancellationToken)
    {
        var snapshot = new AiAnalysisSnapshot
        {
            SessionId = detail.Session.Id, Title = detail.Session.Title, StartedAtUtc = detail.Session.StartedAtUtc, DurationMs = detail.DurationMs,
            RecordedDurationMs = SessionGroupingService.Sources(detail.Session, detail.DurationMs)
                .Sum(source => Math.Max(0, (source.ToMs ?? detail.DurationMs - source.StartOffsetMs) - source.FromMs)),
            TranslationPattern = TranslationDanmuPattern.Resolve(detail.Session, settings),
            SourceFingerprint = Fingerprint(detail, settings),
            EvidenceRelativePath = Path.Combine("ai", "snapshots", Guid.NewGuid().ToString("N") + ".jsonl")
        };
        snapshot.Segments = Enumerable.Range(0, (int)Math.Ceiling(detail.DurationMs / AiAnalysisSnapshot.SEGMENT_DURATION_MS))
            .Select(index => new AiAnalysisSegment
            {
                Index = index, StartMs = (double)index * AiAnalysisSnapshot.SEGMENT_DURATION_MS,
                EndMs = Math.Min(detail.DurationMs, (double)(index + 1) * AiAnalysisSnapshot.SEGMENT_DURATION_MS)
            }).ToList();
        var pattern = TranslationDanmuPattern.Create(snapshot.TranslationPattern);
        var output = MediaPaths.Resolve(paths.DataDirectory, snapshot.EvidenceRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await using (var writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, true), new UTF8Encoding(false)))
            {
                var sources = SessionGroupingService.Sources(detail.Session, detail.DurationMs);
                for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
                {
                    var source = sources[sourceIndex];
                    await foreach (var item in reader.EnumerateAsync(ArchivePath(source.DirectoryPath), cancellationToken))
                    {
                        if (!double.IsFinite(item.OffsetMs) || item.OffsetMs < source.FromMs || source.ToMs is { } end && item.OffsetMs >= end)
                        {
                            continue;
                        }
                        var offset = item.OffsetMs + source.StartOffsetMs;
                        if (offset < 0 || offset >= detail.DurationMs)
                        {
                            continue;
                        }
                        if (item.Kind is not ("Danmu" or "SuperChat"))
                        {
                            continue;
                        }
                        if (!string.IsNullOrEmpty(item.EventId) && !identities.Add(item.EventId))
                        {
                            continue;
                        }
                        var kind = item.Kind == "Danmu" && pattern.IsMatch(item.Text) ? "Translation" : item.Kind;
                        if (string.IsNullOrWhiteSpace(item.Text))
                        {
                            continue;
                        }
                        var evidence = new AiEvidence
                        {
                            Id = $"{sourceIndex}:{item.Sequence}", OffsetMs = offset, Kind = kind,
                            Text = item.Text[..Math.Min(item.Text.Length, 400)]
                        };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(evidence, JsonFiles.Options).AsMemory(), cancellationToken);
                        snapshot.EvidenceCount++;
                        snapshot.Segments[(int)(offset / AiAnalysisSnapshot.SEGMENT_DURATION_MS)].EvidenceCount++;
                    }
                }
            }
            var report = await analytics.GetAsync(detail.Session, paths.RecordsDirectory, detail.DurationMs, snapshot.TranslationPattern, cancellationToken);
            var translations = report.Translations.Buckets.ToDictionary(bucket => bucket.OffsetMs, bucket => bucket.Count);
            var metricBuckets = report.Buckets.ToDictionary(bucket => bucket.OffsetMs);
            snapshot.Buckets = metricBuckets.Keys.Union(translations.Keys).Order().Select(offset =>
            {
                var bucket = metricBuckets.GetValueOrDefault(offset);
                return new AiMetricBucket
                {
                    OffsetMs = offset, DurationMs = bucket?.DurationMs ?? Math.Min(60000, detail.DurationMs - offset),
                    DanmuCount = bucket?.DanmuCount ?? 0, TranslationCount = translations.GetValueOrDefault(offset),
                    SuperChatCount = bucket?.SuperChatCount ?? 0, SuperChatAmount = bucket?.SuperChatAmount ?? 0,
                    OnlineAverage = bucket?.OnlineAverage, OnlinePeak = bucket?.OnlinePeak, OnlineObservedMs = bucket?.OnlineObservedMs ?? 0
                };
            }).ToList();
            snapshot.OnlineAverage = report.Online.Average;
            snapshot.OnlinePeak = report.Online.Peak;
            snapshot.OnlineObservedMs = report.Online.ObservedMs;
            snapshot.OnlineCoverageRate = report.Online.CoverageRate;
            return snapshot;
        }
        catch (RegexMatchTimeoutException)
        {
            File.Delete(output);
            throw new InfrastructureException(MessageCode.TRANSLATION_DANMU_PATTERN_TIMEOUT);
        }
        catch
        {
            File.Delete(output);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<AiEvidencePage> ReadAsync(AiAnalysisSnapshot snapshot, double fromMs, double toMs, string? kind,
        string? query, string? cursor, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 40 || !double.IsFinite(fromMs) || !double.IsFinite(toMs) || fromMs < 0 || toMs > snapshot.DurationMs || fromMs > toMs)
        {
            throw new InvalidDataException("Invalid evidence range.");
        }
        var skip = 0L;
        if (cursor is not null && (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out skip) || skip < 0))
        {
            throw new InvalidDataException("Invalid evidence cursor.");
        }
        var page = new AiEvidencePage();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var characterCount = 0;
        var row = 0L;
        var path = MediaPaths.Resolve(paths.DataDirectory, snapshot.EvidenceRelativePath);
        using var input = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true));
        while (await input.ReadLineAsync(cancellationToken) is { } line)
        {
            row++;
            if (row <= skip)
            {
                continue;
            }
            var item = JsonSerializer.Deserialize<AiEvidence>(line, JsonFiles.Options)!;
            if (item.OffsetMs < fromMs || item.OffsetMs >= toMs || kind is not (null or "All") && item.Kind != kind ||
                query is not null && !item.Text.Contains(query, StringComparison.OrdinalIgnoreCase) || !seen.Add(item.Kind + "\n" + item.Text))
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

    /// <summary>删除未入队或不再需要的精简快照。</summary>
    public void DeleteSnapshot(AiAnalysisSnapshot snapshot) => File.Delete(MediaPaths.Resolve(paths.DataDirectory, snapshot.EvidenceRelativePath));

    /// <summary>生成不包含 API key 的任务复用身份。</summary>
    public static string Identity(string fingerprint, AiSettings settings, string purpose = AiJobPurposes.ANALYSIS) => Hash(JsonSerializer.Serialize(new
    {
        fingerprint, purpose, settings.Provider, settings.BaseUrl, settings.Model, settings.ThinkingMode, settings.ReasoningEffort,
        version = AiAnalysisPolicy.ANALYSIS_VERSION
    }));

    private string ArchivePath(string directory) => MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(directory, "弹幕", "事件.jsonl"));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

}
