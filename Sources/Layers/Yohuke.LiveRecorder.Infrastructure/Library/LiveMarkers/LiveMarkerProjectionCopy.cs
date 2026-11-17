using Yohuke.LiveRecorder.Domain.LiveMarkers;

namespace Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;

/// <summary>归组日志中可幂等恢复的独立附件副本计划。</summary>
public sealed record LiveMarkerProjectionCopy
{
    public string SourceSessionDirectory { get; init; } = "";
    public string SourceFileId { get; init; } = "";
    public string TargetSessionId { get; init; } = "";
    public string TargetSessionDirectory { get; init; } = "";
    public LiveMarkerFileMetadata File { get; init; } = new();
}
