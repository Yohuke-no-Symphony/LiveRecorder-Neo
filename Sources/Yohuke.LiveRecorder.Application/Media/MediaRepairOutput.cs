namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>一个编码区间的独立修复产物。</summary>
public sealed class MediaRepairOutput
{
    public string Path { get; set; } = "";
    public string IndexPath { get; set; } = "";
    public double SourceStartMs { get; set; }
    public double DurationMs { get; set; }
}
