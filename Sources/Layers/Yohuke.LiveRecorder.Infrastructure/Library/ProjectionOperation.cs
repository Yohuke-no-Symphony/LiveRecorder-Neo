using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;

namespace Yohuke.LiveRecorder.Infrastructure.Library;

/// <summary>归组和拆分的文件先行批量发布日志。</summary>
public sealed class ProjectionOperation
{
    public List<SessionManifest> Outputs { get; set; } = [];
    public List<string> SourceIds { get; set; } = [];
    public List<SessionBookmark> Bookmarks { get; set; } = [];
    public List<LiveMarkerProjectionCopy> LiveMarkerCopies { get; set; } = [];
}
