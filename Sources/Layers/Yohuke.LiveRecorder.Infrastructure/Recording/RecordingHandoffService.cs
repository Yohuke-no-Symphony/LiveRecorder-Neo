using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>封口已经排空的本地采集，保留供下一采集者继续的逻辑直播身份。</summary>
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
            session.State = "Waiting";
            session.Lifecycle ??= new RecordingSessionLifecycleState
            {
                LastMediaReceivedAtUtc = session.LastMediaReceivedAtUtc,
                TimeoutAnchorUtc = DateTime.UtcNow,
                LiveTime = session.LiveTime
            };
            var bindings = context.Collection<RecordingCaptureBinding>("recordingCaptureBindings");
            var captures = await bindings.Find(value => value.LogicalSessionId == session.Id).ToListAsync(cancellationToken);
            foreach (var capture in captures.Where(value => value.IsLocal && !value.IsClosed))
            {
                capture.IsClosed = true;
                await bindings.UpdateOneAsync(value => value.Id == capture.Id,
                    Builders<RecordingCaptureBinding>.Update.Set(value => value.IsClosed, true), cancellationToken: cancellationToken);
            }
            await SessionManifestFiles.WriteAsync(Path.Combine(MediaPaths.Resolve(paths.RecordsDirectory, session.DirectoryPath), "场次.json"),
                new() { Session = session, Segments = segments, Captures = captures }, cancellationToken);
            await context.Collection<BroadcastSession>("sessions").UpdateOneAsync(value => value.Id == session.Id && value.EndedAtUtc == null,
                Builders<BroadcastSession>.Update.Set(value => value.State, "Waiting").Set(value => value.Lifecycle, session.Lifecycle),
                cancellationToken: cancellationToken);
            await publisher.PublishAsync("session", session.Id, cancellationToken: cancellationToken);
        }
    }
}
