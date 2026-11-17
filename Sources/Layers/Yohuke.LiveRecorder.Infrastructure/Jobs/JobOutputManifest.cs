using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Infrastructure.Jobs;

/// <summary>带输入身份与产物长度的持久完成清单，用于租约接管后的完整对账。</summary>
public sealed class JobOutputManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string JobId { get; set; } = "";
    public string Kind { get; set; } = "";
    public int SourceRevision { get; set; }
    public string InputFingerprint { get; set; } = "";
    public int Attempt { get; set; }
    public List<string> Paths { get; set; } = [];
    public Dictionary<string, long> ArtifactLengths { get; set; } = new(StringComparer.Ordinal);
    public List<RecordingSegment> RepairedSegments { get; set; } = [];
    public Dictionary<string, string> RepairedAssetSources { get; set; } = new(StringComparer.Ordinal);
    public double ActualStartMs { get; set; }
    public double ActualEndMs { get; set; }

    /// <summary>为当前不可变输入快照建立清单身份，执行租约和重试次数不改变输入指纹。</summary>
    public static JobOutputManifest Create(MediaJob job)
    {
        var input = JsonSerializer.Serialize(new
        {
            job.Id,
            job.Kind,
            job.Mode,
            job.Title,
            job.SourceRevision,
            job.StartMs,
            job.EndMs,
            Sources = job.SourceSegments.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => new
            {
                x.SessionId,
                x.AttemptId,
                x.RelativePath,
                x.StartOffsetMs,
                x.DurationMs,
                x.CommittedBytes,
                x.IsClosed,
                x.Revision,
                x.Codec,
                x.IndexRelativePath,
                x.RootRelativePath,
                x.RootIndexRelativePath,
                x.Id,
                x.CreatedAtUtc
            }).ToArray()
        }, JsonFiles.Options);
        return new()
        {
            JobId = job.Id,
            Kind = job.Kind,
            SourceRevision = job.SourceRevision,
            InputFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))),
            Attempt = job.Attempt,
            ActualStartMs = job.StartMs,
            ActualEndMs = job.EndMs
        };
    }
}
