namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>已探测的音视频参数。</summary>
public sealed class MediaProbe
{
    public double DurationMs { get; set; }
    public string VideoCodec { get; set; } = "";
    public string AudioCodec { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }
    public bool HasAudio { get; set; }
    public long VideoBitrate { get; set; }
    public long AudioBitrate { get; set; }
    public double SourceStartMs { get; set; }
    public bool IsHdr { get; set; }
}
