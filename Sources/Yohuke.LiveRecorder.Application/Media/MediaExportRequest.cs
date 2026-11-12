namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>固定输入快照的切片或合并参数。</summary>
public sealed class MediaExportRequest
{
    public List<MediaInputSpan> Inputs { get; set; } = [];
    public string OutputPath { get; set; } = "";
    public string Mode { get; set; } = "Exact";
    public double StartMs { get; set; }
    public double DurationMs { get; set; }
}
