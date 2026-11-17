using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>Finishes a drained local session before a new remote owner starts an independent event sequence.</summary>
public sealed class RecordingHandoffService(IMongoContext context, IAppPaths paths, IStatusPublisher publisher)
{
    /// <summary>Closes local sessions only after their recording ownership was relinquished or the caller drained its writers.</summary>
    public async Task CloseLocalSessionsAsync(string targetId, CancellationToken cancellationToken)
    {
        var sessions = await context.Collection<BroadcastSession>("sessions").Find(value => value.StreamerId == targetId
            && value.EndedAtUtc == null && value.RecordingAssignmentId == null && !value.IsHidden).ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            var segments = await context.Collection<RecordingSegment>("segments").Find(value => value.SessionId == session.Id).ToListAsync(cancellationToken);
            if (segments.Any(value => !value.IsClosed))
            {
                throw new InfrastructureException("本地录制尚未封口，不能转交房间。", 409);
            }
            session.State = "Completed";
            session.EndedAtUtc = session.LastMediaAtUtc;
            await SessionManifestFiles.WriteAsync(Path.Combine(MediaPaths.Resolve(paths.RecordsDirectory, session.DirectoryPath), "场次.json"),
                new() { Session = session, Segments = segments }, cancellationToken);
            await context.Collection<BroadcastSession>("sessions").UpdateOneAsync(value => value.Id == session.Id && value.EndedAtUtc == null,
                Builders<BroadcastSession>.Update.Set(value => value.State, "Completed").Set(value => value.EndedAtUtc, session.EndedAtUtc),
                cancellationToken: cancellationToken);
            await publisher.PublishAsync("session", session.Id, cancellationToken: cancellationToken);
        }
    }
}
