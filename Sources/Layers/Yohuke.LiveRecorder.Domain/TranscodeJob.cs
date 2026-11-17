using System.Text.Json.Serialization;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Domain;

/// <summary>以物理源版本和编码配方去重的远端播放派生任务。</summary>
public sealed class TranscodeJob : Entity
{
    public string SessionId { get; set; } = "";
    public string Title { get; set; } = "";
    public string AssetId { get; set; } = "";
    public string SourceVersion { get; set; } = "";
    public string RecipeHash { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public TranscodeProfile Profile { get; set; } = new();
    public string State { get; set; } = "Queued";
    public string Stage { get; set; } = "WaitingForWorker";
    public double Progress { get; set; }
    public WorkerJobActivity? Activity { get; set; }
    public DateTime? LastProgressAtUtc { get; set; }
    public string? WorkerId { get; set; }
    public string? Encoder { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    public int Attempt { get; set; }
    [JsonIgnore]
    public string? LeaseTokenHash { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public bool CancelRequested { get; set; }
    public bool Force { get; set; }
    public bool PublicationReady { get; set; }
    [JsonIgnore]
    public string? PublicationDirectory { get; set; }
}
