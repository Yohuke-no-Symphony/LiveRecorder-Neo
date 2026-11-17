using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>Preserves local recording target status and room metadata semantics for remote capture progress.</summary>
public sealed class RecordingTargetStatusService(IMongoContext context, IStatusPublisher publisher)
{
    /// <summary>Applies an authenticated owner's capture state without treating every heartbeat as a successful room lookup.</summary>
    public async Task ApplyAsync(RecordingAssignmentRecord assignment, RecordingRoomProgress progress, CancellationToken cancellationToken)
    {
        var targets = context.Collection<Streamer>("streamers");
        var filter = Builders<Streamer>.Filter.Where(value => value.Id == assignment.TargetId && value.RoomId == assignment.RoomId);
        var state = progress.State switch
        {
            "Recording" => "Recording",
            "Draining" => "Uploading",
            "Retrying" or "StorageFull" or "Paused" => "Retrying",
            _ => "Waiting"
        };
        var error = progress.Error is null ? null : progress.Error[..Math.Min(progress.Error.Length, 4096)];
        var errorCode = progress.ErrorCode is { Length: > 0 and <= 128 } code ? code : null;
        if (progress.State == "StorageFull")
        {
            errorCode ??= nameof(MessageCode.RECORDING_DISK_SPACE_LOW);
        }
        var errorArguments = (progress.ErrorArguments ?? []).OfType<string>().Take(8)
            .Select(value => value[..Math.Min(value.Length, 1024)]).ToList();
        await targets.UpdateOneAsync(filter, Builders<Streamer>.Update.Set(value => value.State, state)
            .Set(value => value.Error, error).Set(value => value.ErrorCode, errorCode).Set(value => value.ErrorArguments, errorArguments),
            cancellationToken: cancellationToken);
        if (progress.Room is { } room && IsValidRoom(room, assignment.RoomId))
        {
            var checkedAt = room.CheckedAtUtc > DateTime.UtcNow ? DateTime.UtcNow : room.CheckedAtUtc;
            var fresh = filter & Builders<Streamer>.Filter.Where(value => value.LastCheckedAtUtc == null || value.LastCheckedAtUtc <= checkedAt);
            await targets.UpdateOneAsync(fresh & Builders<Streamer>.Filter.Ne(value => value.AvatarUrl, room.AvatarUrl),
                Builders<Streamer>.Update.Set(value => value.AvatarUrl, room.AvatarUrl).Set(value => value.AvatarRelativePath, null),
                cancellationToken: cancellationToken);
            await targets.UpdateOneAsync(fresh, Builders<Streamer>.Update.Set(value => value.Uid, room.Uid).Set(value => value.Name, room.Name)
                .Set(value => value.IsLive, room.IsLive).Set(value => value.LastCheckedAtUtc, checkedAt), cancellationToken: cancellationToken);
        }
        await publisher.PublishAsync("streamer", assignment.TargetId, cancellationToken: cancellationToken);
    }

    private static bool IsValidRoom(RecordingRoomSnapshot room, long roomId)
    {
        return room.RoomId == roomId && room.Uid > 0 && room.Name is { Length: > 0 and <= 256 }
            && room.AvatarUrl is { Length: <= 4096 } && room.CheckedAtUtc != default
            && (room.AvatarUrl.Length == 0 || Uri.TryCreate(room.AvatarUrl, UriKind.Absolute, out var avatar)
                && avatar.Scheme is "https" or "http" && string.IsNullOrEmpty(avatar.UserInfo));
    }
}
