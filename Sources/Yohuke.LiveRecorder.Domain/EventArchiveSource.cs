namespace Yohuke.LiveRecorder.Domain;

/// <summary>归组或拆分后保留原事件文件的时间投影。</summary>
public sealed class EventArchiveSource
{
    public string DirectoryPath { get; set; } = "";
    public double StartOffsetMs { get; set; }
    public double FromMs { get; set; }
    public double? ToMs { get; set; }
}
