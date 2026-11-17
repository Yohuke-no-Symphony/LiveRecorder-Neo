using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>按库操作的持久意图复核受影响资产，避免在调度循环中扫描整个录播库。</summary>
public sealed class TranscodeLifecycle(IMongoContext context, IAppPaths paths)
{
    /// <summary>持久记录需要复核引用的资产，重复记录不会扩张队列。</summary>
    public async Task MarkDirtyAsync(IReadOnlyCollection<string> assetIds, CancellationToken cancellationToken = default)
    {
        if (assetIds.Count == 0)
        {
            return;
        }
        var registered = await context.Collection<MediaAsset>("mediaAssets")
            .Find(Builders<MediaAsset>.Filter.In(x => x.Id, assetIds)).Project(x => x.Id).ToListAsync(cancellationToken);
        foreach (var id in registered)
        {
            await context.Collection<TranscodeAssetReconciliation>("transcodeAssetReconciliation")
                .ReplaceOneAsync(x => x.Id == id, new() { Id = id }, new ReplaceOptions { IsUpsert = true }, cancellationToken);
        }
    }

    /// <summary>在库管理锁内复核明确的资产，或消费至多一百条待恢复意图。</summary>
    public async Task ReconcileUnderLibraryLockAsync(CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? assetIds = null)
    {
        if (assetIds is not null)
        {
            await MarkDirtyAsync(assetIds, cancellationToken);
        }
        var pending = context.Collection<TranscodeAssetReconciliation>("transcodeAssetReconciliation");
        var filter = assetIds is null ? FilterDefinition<TranscodeAssetReconciliation>.Empty
            : Builders<TranscodeAssetReconciliation>.Filter.In(x => x.Id, assetIds);
        var intents = await pending.Find(filter).SortBy(x => x.UpdatedAtUtc).Limit(100).ToListAsync(cancellationToken);
        foreach (var intent in intents)
        {
            var asset = await context.Collection<MediaAsset>("mediaAssets").Find(x => x.Id == intent.Id).FirstOrDefaultAsync(cancellationToken);
            if (asset is not null)
            {
                await ReconcileAssetAsync(asset, cancellationToken);
            }
            await pending.DeleteOneAsync(x => x.Id == intent.Id && x.UpdatedAtUtc == intent.UpdatedAtUtc, cancellationToken);
        }
    }

    private async Task ReconcileAssetAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        var segments = await context.Collection<RecordingSegment>("segments").Find(x => x.AssetId == asset.Id).ToListAsync(cancellationToken);
        var sessionIds = segments.Select(x => x.SessionId).Distinct().ToArray();
        var sessions = await context.Collection<BroadcastSession>("sessions").Find(x => sessionIds.Contains(x.Id)).ToListAsync(cancellationToken);
        var entityIds = sessionIds.Concat(segments.Select(x => x.Id)).ToArray();
        var ancestorDirectories = new List<string>();
        for (var directory = Path.GetDirectoryName(asset.SourceRelativePath); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            ancestorDirectories.Add(directory);
        }
        var trash = await context.Collection<TrashEntry>("trash").Find(x =>
            (entityIds.Contains(x.EntityId) && (x.Kind == "Session" || x.Kind == "Segment"))
            || x.Kind == "Segment" && x.OriginalRelativePath == asset.SourceRelativePath
            || x.Kind == "Session" && ancestorDirectories.Contains(x.OriginalRelativePath)).ToListAsync(cancellationToken);
        var active = false;
        var retained = trash.Count > 0;
        foreach (var segment in segments)
        {
            var session = sessions.FirstOrDefault(x => x.Id == segment.SessionId);
            if (session is null || segment.Revision != session.MediaRevision || session.IsHidden || !session.ProjectionReady)
            {
                continue;
            }
            retained = true;
            active |= !session.IsTrashed && !trash.Any(x => x.EntityId == session.Id || x.EntityId == segment.Id);
        }
        var jobs = context.Collection<TranscodeJob>("transcodeJobs");
        await jobs.UpdateManyAsync(x => x.AssetId == asset.Id && x.SourceVersion != asset.SourceVersion
            && (x.State == "Queued" || x.State == "Running" || x.State == "Publishing"),
            Unavailable("SourceReplaced"), cancellationToken: cancellationToken);
        if (asset.Suspended == active)
        {
            await context.Collection<MediaAsset>("mediaAssets").UpdateOneAsync(x => x.Id == asset.Id && x.SourceVersion == asset.SourceVersion,
                Builders<MediaAsset>.Update.Set(x => x.Suspended, !active), cancellationToken: cancellationToken);
        }
        if (!active)
        {
            await jobs.UpdateManyAsync(x => x.AssetId == asset.Id && (x.State == "Queued" || x.State == "Running" || x.State == "Publishing"),
                Unavailable("SourceUnavailable"), cancellationToken: cancellationToken);
        }
        else if (asset.Suspended)
        {
            await jobs.UpdateManyAsync(x => x.AssetId == asset.Id && x.SourceVersion == asset.SourceVersion
                && x.State == "Cancelled" && x.ErrorCode == "SourceUnavailable" && !x.CancelRequested,
                TranscodeJobScheduler.Reset(), cancellationToken: cancellationToken);
        }
        if (!retained)
        {
            var directory = MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(".derived", asset.Id));
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
            await context.Collection<PlaybackRendition>("playbackRenditions").DeleteManyAsync(x => x.AssetId == asset.Id, cancellationToken);
            await context.Collection<PlaybackRenditionFile>("renditionFiles").DeleteManyAsync(x => x.AssetId == asset.Id, cancellationToken);
            await context.Collection<MediaAsset>("mediaAssets").DeleteOneAsync(x => x.Id == asset.Id, cancellationToken);
        }
    }

    private static UpdateDefinition<TranscodeJob> Unavailable(string reason) => Builders<TranscodeJob>.Update
        .Set(x => x.State, "Cancelled").Set(x => x.Stage, reason).Set(x => x.ErrorCode, reason)
        .Set(x => x.LeaseTokenHash, null).Set(x => x.LeaseUntilUtc, null).Set(x => x.FinishedAtUtc, DateTime.UtcNow);
}
