using Yohuke.LiveRecorder.Application.Localization;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Library;

/// <summary>读取录播库主播、当前媒体版本并构建共用时间轴。</summary>
public sealed class LibraryService(IMongoContext context, TimeProvider timeProvider)
{
    /// <summary>读取仍在监控或存在可见录播场次的主播。</summary>
    public async Task<IReadOnlyList<Streamer>> GetStreamersAsync(CancellationToken cancellationToken = default)
    {
        var sessions = Builders<BroadcastSession>.Filter.Where(x => !x.IsTrashed && !x.IsHidden && x.ProjectionReady);
        using var cursor = await context.Collection<BroadcastSession>("sessions")
            .DistinctAsync(x => x.StreamerId, sessions, cancellationToken: cancellationToken);
        var streamerIds = await cursor.ToListAsync(cancellationToken);
        var filter = Builders<Streamer>.Filter.Ne(x => x.IsMonitored, false) |
                     Builders<Streamer>.Filter.In(x => x.Id, streamerIds);
        return await context.Collection<Streamer>("streamers").Find(filter).SortBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    /// <summary>读取可访问的场次及当前媒体分段。</summary>
    public async Task<SessionDetail> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var session = await context.Collection<BroadcastSession>("sessions").Find(x => x.Id == id && !x.IsTrashed && x.ProjectionReady).FirstOrDefaultAsync(cancellationToken)
                      ?? throw new InfrastructureException(MessageCode.LIBRARY_SESSION_NOT_FOUND, 404);
        var segments = await context.Collection<RecordingSegment>("segments").Find(x => x.SessionId == id && x.Revision == session.MediaRevision).ToListAsync(cancellationToken);
        return SessionTimeline.Build(session, segments, timeProvider.GetUtcNow().UtcDateTime);
    }
}
