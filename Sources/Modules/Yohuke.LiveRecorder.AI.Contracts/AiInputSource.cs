namespace Yohuke.LiveRecorder.AI.Contracts;

/// <summary>由主站稳定 ID 授权下载的源档案，不包含服务器路径。</summary>
public sealed record AiInputSource
{
    public string Id { get; set; } = "";
    public double StartOffsetMs { get; set; }
    public double FromMs { get; set; }
    public double? ToMs { get; set; }
    public long Length { get; set; }
    public long LastWriteUtcTicks { get; set; }
}
