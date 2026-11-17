using System.Globalization;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Infrastructure.AI;

/// <summary>固定归档来源身份及有效播放区间，不在网页请求中扫描或分析正文。</summary>
public sealed class AiInputManifestFactory(IAppPaths paths, AiArchiveDataSource data)
{
    /// <summary>比较当前归档版本和规则身份，不读取正文。</summary>
    public string Fingerprint(SessionDetail detail, ServerSettings settings) => data.Fingerprint(detail, settings);

    /// <summary>记录新任务的不可变来源清单和有效媒体覆盖时长。</summary>
    public AiInputManifest Create(SessionDetail detail, ServerSettings settings, out List<string> sourcePaths)
    {
        sourcePaths = [];
        var manifest = new AiInputManifest
        {
            Fingerprint = data.Fingerprint(detail, settings), Title = detail.Session.Title,
            StartedAtUtc = detail.Session.StartedAtUtc, DurationMs = detail.DurationMs,
            TranslationPattern = TranslationDanmuPattern.Resolve(detail.Session, settings),
            RecordedDurationMs = RecordedDuration(detail)
        };
        var sources = SessionGroupingService.Sources(detail.Session, detail.DurationMs);
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var relative = Path.Combine(source.DirectoryPath, "弹幕", "事件.jsonl");
            var file = new FileInfo(MediaPaths.Resolve(paths.RecordsDirectory, relative));
            sourcePaths.Add(relative);
            manifest.Sources.Add(new()
            {
                Id = index.ToString(CultureInfo.InvariantCulture), StartOffsetMs = source.StartOffsetMs,
                FromMs = source.FromMs, ToMs = source.ToMs, Length = file.Exists ? CommittedLength(file.FullName) : 0,
                LastWriteUtcTicks = file.Exists ? file.LastWriteTimeUtc.Ticks : 0
            });
        }
        return manifest;
    }

    /// <summary>将升级前已准备好的精简证据包装为只读输入。</summary>
    public AiInputManifest Legacy(AiAnalysisJob job)
    {
        var file = new FileInfo(MediaPaths.Resolve(paths.DataDirectory, job.Snapshot.EvidenceRelativePath));
        return new()
        {
            Fingerprint = job.Snapshot.SourceFingerprint, Title = job.Snapshot.Title,
            StartedAtUtc = job.Snapshot.StartedAtUtc, DurationMs = job.Snapshot.DurationMs,
            RecordedDurationMs = job.Snapshot.RecordedDurationMs, TranslationPattern = job.Snapshot.TranslationPattern,
            PreparedSnapshot = true,
            Sources = [new() { Id = "0", Length = file.Exists ? CommittedLength(file.FullName) : 0,
                LastWriteUtcTicks = file.Exists ? file.LastWriteTimeUtc.Ticks : 0 }]
        };
    }

    private static double RecordedDuration(SessionDetail detail)
    {
        var cursor = 0d;
        var duration = 0d;
        foreach (var segment in detail.Segments.Where(value => value.DurationMs > 0).OrderBy(value => value.StartOffsetMs))
        {
            var start = Math.Max(0, segment.StartOffsetMs);
            var end = Math.Min(detail.DurationMs, segment.StartOffsetMs + segment.DurationMs);
            if (end > Math.Max(cursor, start))
            {
                duration += end - Math.Max(cursor, start);
            }
            cursor = Math.Max(cursor, end);
        }
        return duration;
    }

    private static long CommittedLength(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var position = input.Length;
        var buffer = new byte[4096];
        while (position > 0)
        {
            var count = (int)Math.Min(position, buffer.Length);
            position -= count;
            input.Position = position;
            input.ReadExactly(buffer.AsSpan(0, count));
            for (var index = count - 1; index >= 0; index--)
            {
                if (buffer[index] == (byte)'\n')
                {
                    return position + index + 1;
                }
            }
        }
        return 0;
    }
}
