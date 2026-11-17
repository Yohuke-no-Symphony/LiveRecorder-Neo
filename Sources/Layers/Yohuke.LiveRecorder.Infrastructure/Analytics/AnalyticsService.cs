using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>按需从事件文件重建版本化观测统计，所有统计缓存仍为磁盘文件。</summary>
public sealed class AnalyticsService(EventArchiveReader reader)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>按来源区间投影统计，恢复裁切范围之前的金融去重基线。</summary>
    public Task<AnalyticsReport> GetAsync(BroadcastSession session, string storageRoot,
        CancellationToken cancellationToken = default)
    {
        return GetSessionAsync(session, storageRoot, null, null, null, null, cancellationToken);
    }

    /// <summary>按统一播放终点排除已结束场次的尾部互动，再计算金融去重和用户统计。</summary>
    public Task<AnalyticsReport> GetAsync(BroadcastSession session, string storageRoot, double playbackEndMs,
        CancellationToken cancellationToken = default)
    {
        return GetSessionAsync(session, storageRoot, playbackEndMs, null, null, null, cancellationToken);
    }

    /// <summary>按有效同传正文规则分流统计，并沿用播放边界及金融去重基线。</summary>
    public Task<AnalyticsReport> GetAsync(BroadcastSession session, string storageRoot, double playbackEndMs,
        string translationPattern, CancellationToken cancellationToken = default)
    {
        return GetSessionAsync(session, storageRoot, playbackEndMs, TranslationDanmuPattern.Create(translationPattern), null, null, cancellationToken);
    }

    /// <summary>按录像绝对偏移重新计算所选半开区间，选区结果不替换整场磁盘缓存。</summary>
    public Task<AnalyticsReport> GetAsync(BroadcastSession session, string storageRoot, double playbackEndMs,
        string translationPattern, double? fromMs, double? toMs, CancellationToken cancellationToken = default)
    {
        if (fromMs is { } from && (!double.IsFinite(from) || from < 0) ||
            toMs is { } to && (!double.IsFinite(to) || to < 0 || to < (fromMs ?? 0)))
        {
            throw new InfrastructureException(MessageCode.LIBRARY_EVENT_RANGE_INVALID);
        }
        return GetSessionAsync(session, storageRoot, playbackEndMs, TranslationDanmuPattern.Create(translationPattern), fromMs, toMs, cancellationToken);
    }

    private async Task<AnalyticsReport> GetSessionAsync(BroadcastSession session, string storageRoot, double? playbackEndMs,
        Regex? translationPattern, double? fromMs, double? toMs, CancellationToken cancellationToken)
    {
        return (await ReadSessionAsync(session, storageRoot, playbackEndMs, translationPattern, fromMs, toMs, false, cancellationToken)).Report;
    }

    internal Task<AnalyticsBuildData> BuildExportAsync(SessionDetail detail, string storageRoot, string translationPattern,
        double? fromMs, double? toMs, CancellationToken cancellationToken)
    {
        return ReadSessionAsync(detail.Session, storageRoot, detail.DurationMs, TranslationDanmuPattern.Create(translationPattern),
            fromMs, toMs, true, cancellationToken);
    }

    private Task<AnalyticsBuildData> ReadSessionAsync(BroadcastSession session, string storageRoot, double? playbackEndMs,
        Regex? translationPattern, double? fromMs, double? toMs, bool export, CancellationToken cancellationToken)
    {
        var sources = SessionGroupingService.Sources(session, playbackEndMs).Select(source =>
            (Path: Path.Combine(MediaPaths.Resolve(storageRoot, source.DirectoryPath), "弹幕", "事件.jsonl"),
                Offset: source.StartOffsetMs, From: source.FromMs, To: source.ToMs)).ToList();
        var output = Path.Combine(MediaPaths.Resolve(storageRoot, session.DirectoryPath), "分析", "summary.json");
        double? fixedEndMs = session.EndedAtUtc is { } ended ? Math.Max(0, (ended - session.StartedAtUtc).TotalMilliseconds) : null;
        if (fixedEndMs is { } end && playbackEndMs is { } playbackEnd)
        {
            fixedEndMs = Math.Min(end, double.IsFinite(playbackEnd) ? Math.Max(0, playbackEnd) : 0);
        }
        if (fixedEndMs is null && sources.All(source => source.To.HasValue))
        {
            fixedEndMs = sources.Select(source => Math.Max(0, source.Offset + source.To!.Value)).DefaultIfEmpty().Max();
        }
        var liveStart = fixedEndMs is null && session.StartedAtUtc != default ? session.StartedAtUtc : (DateTime?)null;
        return ReadAsync(sources, output, translationPattern, fixedEndMs, liveStart, fromMs, toMs, export, cancellationToken);
    }

    /// <summary>读取完整场次文件统计，文件增长时最多每三十秒重建一次。</summary>
    public async Task<AnalyticsReport> GetAsync(string sessionDirectory, CancellationToken cancellationToken = default)
    {
        return (await ReadAsync([(Path.Combine(sessionDirectory, "弹幕", "事件.jsonl"), 0d, 0d, null)],
            Path.Combine(sessionDirectory, "分析", "summary.json"), null, null, null, null, null, false, cancellationToken)).Report;
    }

    private async Task<AnalyticsBuildData> ReadAsync(
        IReadOnlyList<(string Path, double Offset, double From, double? To)> sources, string output,
        Regex? translationPattern, double? fixedEndMs, DateTime? liveStart, double? fromMs, double? toMs,
        bool export, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var identity = Hash(JsonSerializer.Serialize(new
            {
                Sources = sources.Select(source => new { source.Path, source.Offset, source.From, source.To }),
                TranslationPattern = translationPattern?.ToString(),
                FixedEndMs = fixedEndMs,
                LiveStart = liveStart,
                FromMs = fromMs,
                ToMs = toMs
            }, JsonFiles.Options));
            var files = sources.Select(source => new FileInfo(source.Path)).ToArray();
            var snapshotLengths = files.Select(file => file.Exists ? file.Length : 0).ToArray();
            var length = snapshotLengths.Sum();
            var fingerprint = Hash(JsonSerializer.Serialize(files.Select(file => new
            {
                file.FullName, Length = file.Exists ? file.Length : 0,
                Modified = file.Exists ? file.LastWriteTimeUtc.Ticks : 0
            }), JsonFiles.Options));
            var selection = fromMs.HasValue || toMs.HasValue;
            var saved = selection || export ? null : await ReadCacheAsync(output, cancellationToken);
            var now = DateTime.UtcNow;
            var fresh = saved is not null && saved.BuiltAtUtc <= now && now - saved.BuiltAtUtc < TimeSpan.FromSeconds(30);
            if (saved is not null && saved.SchemaVersion == AnalyticsReport.CURRENT_SCHEMA_VERSION &&
                AnalyticsCacheValidator.IsComplete(saved) && saved.SourceIdentity == identity
                && (fresh || liveStart is null && saved.SourceFingerprint == fingerprint))
            {
                return new() { Report = saved };
            }

            var projectionSources = sources.Select((source, index) =>
            {
                var snapshotLength = snapshotLengths[index];
                return new AnalyticsProjectionSource
                {
                    OffsetMs = source.Offset,
                    FromMs = source.From,
                    ToMs = source.To,
                    ReadAsync = token => reader.EnumerateAsync(source.Path, snapshotLength, token)
                };
            }).ToArray();
            var projection = await new AnalyticsProjectionEngine().BuildAsync(projectionSources, new()
            {
                FixedEndMs = fixedEndMs,
                InitialEndMs = liveStart is { } start ? Math.Max(0, (now - start).TotalMilliseconds) : 0,
                FromMs = fromMs,
                ToMs = toMs,
                TranslationPattern = translationPattern,
                CaptureContributions = export
            }, cancellationToken);
            var result = projection.Report;
            result.SourceBytes = length;
            result.SourceIdentity = identity;
            result.SourceFingerprint = fingerprint;
            if (!selection && !export)
            {
                await JsonFiles.WriteAsync(output, result, cancellationToken);
            }
            return new()
            {
                Report = result,
                Contributions = export ? AnalyticsContributionBuilder.Build(projection.Accumulator, "All") : null
            };
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<AnalyticsReport?> ReadCacheAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<AnalyticsReport>(stream, JsonFiles.Options, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
