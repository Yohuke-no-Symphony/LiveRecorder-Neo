using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Library;

/// <summary>读取当前媒体版本并构建共用时间轴。</summary>
public sealed class LibraryService(MongoContext context, TimeProvider timeProvider)
{
    /// <summary>读取可访问的场次及当前媒体分段。</summary>
    public async Task<SessionDetail> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var session = await context.Collection<BroadcastSession>("sessions").Find(x => x.Id == id && !x.IsTrashed && x.ProjectionReady).FirstOrDefaultAsync(cancellationToken)
                      ?? throw new InfrastructureException("场次不存在。", 404);
        var segments = await context.Collection<RecordingSegment>("segments").Find(x => x.SessionId == id && x.Revision == session.MediaRevision).ToListAsync(cancellationToken);
        return SessionTimeline.Build(session, segments, timeProvider.GetUtcNow().UtcDateTime);
    }
}
