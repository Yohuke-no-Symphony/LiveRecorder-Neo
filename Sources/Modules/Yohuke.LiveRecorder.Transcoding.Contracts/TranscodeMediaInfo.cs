namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>转码输入或成品的可验证媒体参数。</summary>
public sealed record TranscodeMediaInfo
{
    public double DurationMs { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }
    public long VideoBitrate { get; set; }
    public long AudioBitrate { get; set; }
    public bool HasAudio { get; set; }
    public string VideoCodec { get; set; } = "h264";
    public string AudioCodec { get; set; } = "aac";
    public bool IsHdr { get; set; }
    public double SourceStartMs { get; set; }
    public double? RawVideoStartMs { get; set; }
    public double? FirstVideoSampleDurationMs { get; set; }
    public double? RawVideoEndMs { get; set; }
}
