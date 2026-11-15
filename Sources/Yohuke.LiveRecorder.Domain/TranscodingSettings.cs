using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Domain;

/// <summary>持久化的播放档位和新录播自动转码策略。</summary>
public sealed class TranscodingSettings
{
    public bool Enabled { get; set; } = true;
    public List<TranscodeProfile> Profiles { get; set; } = [new()];
    public List<string> QualityOrder { get; set; } = ["original", "720p"];
    public string DefaultPlaybackQualityId { get; set; } = "720p";
}
