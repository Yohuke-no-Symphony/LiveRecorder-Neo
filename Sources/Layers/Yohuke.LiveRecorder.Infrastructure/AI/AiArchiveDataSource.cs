using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Infrastructure.AI;

/// <summary>根据归档元数据、播放范围和分析设置计算任务输入及复用身份。</summary>
public sealed class AiArchiveDataSource(IAppPaths paths)
{
    /// <summary>计算数据、播放范围和实际同传规则的身份，不读取正文或创建缓存。</summary>
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
                return new
                {
                    source.DirectoryPath, source.FromMs, source.ToMs, source.StartOffsetMs,
                    length = file.Exists ? file.Length : 0, modified = file.Exists ? file.LastWriteTimeUtc.Ticks : 0
                };
            })
        }));
    }

    /// <summary>生成不包含 API key 的任务复用身份。</summary>
    public static string Identity(string fingerprint, AiSettings settings, string purpose = AiJobPurposes.ANALYSIS) => Hash(JsonSerializer.Serialize(new
    {
        fingerprint, purpose, settings.Provider, settings.BaseUrl, settings.Model, settings.ThinkingMode, settings.ReasoningEffort,
        version = AiAnalysisPolicy.ANALYSIS_VERSION
    }));

    private string ArchivePath(string directory) => MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(directory, "弹幕", "事件.jsonl"));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
