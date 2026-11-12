using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>可脱离数据库重建媒体目录的场次清单。</summary>
public sealed class SessionManifest
{
    public int SchemaVersion { get; set; } = 1;
    public BroadcastSession Session { get; set; } = new();
    public List<RecordingSegment> Segments { get; set; } = [];
}
