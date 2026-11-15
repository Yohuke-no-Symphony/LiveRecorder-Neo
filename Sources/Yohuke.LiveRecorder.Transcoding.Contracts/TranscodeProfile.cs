namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>平台无关的播放转码档位。</summary>
public sealed record TranscodeProfile
{
    public string Id { get; set; } = "720p";
    public string Name { get; set; } = "720p";
    public int Height { get; set; } = 720;
    public int VideoBitrateKbps { get; set; } = 2500;
    public double? MaxFrameRate { get; set; }
    public int AudioBitrateKbps { get; set; } = 128;
    public bool Enabled { get; set; } = true;
}
