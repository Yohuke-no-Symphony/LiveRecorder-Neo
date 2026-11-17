using System.Security.Cryptography;
using System.Text;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>将场次投影解析为稳定物理媒体，并在每次远端访问前校验源版本。</summary>
public sealed class TranscodeAssetRegistry(IMongoContext context, IAppPaths paths)
{
    /// <summary>建立媒体引用和派生队列的有界查询索引。</summary>
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        await context.Collection<RecordingSegment>("segments").Indexes.CreateManyAsync([
            new(Builders<RecordingSegment>.IndexKeys.Ascending(x => x.AssetId), new CreateIndexOptions { Name = "asset_reference" }),
            new(Builders<RecordingSegment>.IndexKeys.Ascending(x => x.RootRelativePath), new CreateIndexOptions { Name = "asset_root_path" })
        ], cancellationToken);
        await context.Collection<TranscodeJob>("transcodeJobs").Indexes.CreateManyAsync([
            new(Builders<TranscodeJob>.IndexKeys.Ascending(x => x.State).Ascending(x => x.LeaseUntilUtc).Ascending(x => x.CreatedAtUtc), new CreateIndexOptions { Name = "transcode_queue" }),
            new(Builders<TranscodeJob>.IndexKeys.Ascending(x => x.Profile.Id).Ascending(x => x.State).Ascending(x => x.CreatedAtUtc).Ascending(x => x.Id), new CreateIndexOptions { Name = "transcode_profile_queue" }),
            new(Builders<TranscodeJob>.IndexKeys.Ascending(x => x.WorkerId).Ascending(x => x.State), new CreateIndexOptions { Name = "worker_jobs" }),
            new(Builders<TranscodeJob>.IndexKeys.Ascending(x => x.AssetId).Ascending(x => x.SourceVersion), new CreateIndexOptions { Name = "asset_jobs" }),
            new(Builders<TranscodeJob>.IndexKeys.Ascending(x => x.PublicationDirectory), new CreateIndexOptions { Name = "publication_directory" }),
            new(Builders<TranscodeJob>.IndexKeys.Ascending(x => x.State).Ascending(x => x.PublicationReady), new CreateIndexOptions { Name = "publication_recovery" })
        ], cancellationToken);
        await context.Collection<TrashEntry>("trash").Indexes.CreateOneAsync(new CreateIndexModel<TrashEntry>(
            Builders<TrashEntry>.IndexKeys.Ascending(x => x.OriginalRelativePath), new CreateIndexOptions { Name = "trash_source_path" }), cancellationToken: cancellationToken);
        await context.Collection<BroadcastSession>("sessions").Indexes.CreateOneAsync(new CreateIndexModel<BroadcastSession>(
            Builders<BroadcastSession>.IndexKeys.Ascending(x => x.AutoTranscodeEligible).Ascending(x => x.AutoTranscodeScheduled), new CreateIndexOptions { Name = "transcode_auto_intent" }), cancellationToken: cancellationToken);
        await context.Collection<PlaybackRendition>("playbackRenditions").Indexes.CreateOneAsync(new CreateIndexModel<PlaybackRendition>(
            Builders<PlaybackRendition>.IndexKeys.Ascending(x => x.AssetId).Ascending(x => x.SourceVersion).Ascending(x => x.State),
            new CreateIndexOptions { Name = "asset_renditions" }), cancellationToken: cancellationToken);
        await context.Collection<PlaybackRenditionFile>("renditionFiles").Indexes.CreateManyAsync([
            new(Builders<PlaybackRenditionFile>.IndexKeys.Ascending(x => x.AssetId), new CreateIndexOptions { Name = "asset_files" }),
            new(Builders<PlaybackRenditionFile>.IndexKeys.Ascending(x => x.RenditionId), new CreateIndexOptions { Name = "rendition_files" })
        ], cancellationToken);
    }

    /// <summary>登记关闭分段当前的物理文件版本，不读取整段媒体内容。</summary>
    public async Task<MediaAsset> EnsureAsync(BroadcastSession session, RecordingSegment segment,
        CancellationToken cancellationToken = default)
    {
        var asset = Describe(session, segment);
        var assets = context.Collection<MediaAsset>("mediaAssets");
        var previous = await assets.Find(x => x.Id == asset.Id).FirstOrDefaultAsync(cancellationToken);
        if (previous?.SourceVersion == asset.SourceVersion)
        {
            asset.SourceSha256 = previous.SourceSha256;
            asset.MediaInfo = previous.MediaInfo;
            asset.CreatedAtUtc = previous.CreatedAtUtc;
            asset.Suspended = previous.Suspended;
        }
        await assets.ReplaceOneAsync(x => x.Id == asset.Id, asset, new ReplaceOptions { IsUpsert = true }, cancellationToken);
        await BindReferencesAsync(asset, segment.Id, cancellationToken);
        await new TranscodeLifecycle(context, paths).MarkDirtyAsync([asset.Id], cancellationToken);
        return asset;
    }

    /// <summary>读取当前物理版本；播放授权路径不写数据库或遍历其他场次。</summary>
    public MediaAsset Describe(BroadcastSession session, RecordingSegment segment)
    {
        if (!segment.IsClosed || segment.CommittedBytes <= 0 || segment.DurationMs <= 0)
        {
            throw new InvalidOperationException("Only committed closed media can be transcoded.");
        }
        var relative = RelativePath(session, segment);
        var file = new FileInfo(MediaPaths.Resolve(paths.RecordsDirectory, relative));
        if (!file.Exists || file.Length < segment.CommittedBytes)
        {
            throw new FileNotFoundException("The committed recording is unavailable.");
        }
        return new()
        {
            Id = AssetId(session, segment),
            SourceRelativePath = relative,
            SourceLength = file.Length,
            SourceWriteTicks = file.LastWriteTimeUtc.Ticks,
            CommittedBytes = segment.CommittedBytes,
            DurationMs = segment.DurationMs,
            SourceVersion = Hash(FormattableString.Invariant($"{file.Length}:{file.LastWriteTimeUtc.Ticks}:{segment.CommittedBytes}"))
        };
    }

    /// <summary>拒绝不可见、被替换或已移入回收站的源，仍允许其他逻辑场次共享的文件。</summary>
    public async Task<MediaAsset> RequireActiveAsync(string assetId, string sourceVersion,
        CancellationToken cancellationToken = default)
    {
        var asset = await context.Collection<MediaAsset>("mediaAssets")
            .Find(x => x.Id == assetId && x.SourceVersion == sourceVersion && !x.Suspended)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The source media version is no longer active.");
        var segments = await context.Collection<RecordingSegment>("segments")
            .Find(x => x.AssetId == assetId && x.IsClosed).ToListAsync(cancellationToken);
        var ids = segments.Select(x => x.SessionId).Distinct().ToArray();
        var sessions = await context.Collection<BroadcastSession>("sessions")
            .Find(x => ids.Contains(x.Id) && !x.IsTrashed && !x.IsHidden && x.ProjectionReady && x.EndedAtUtc != null)
            .ToListAsync(cancellationToken);
        var entityIds = ids.Concat(segments.Select(x => x.Id)).ToArray();
        var trash = await context.Collection<TrashEntry>("trash")
            .Find(x => entityIds.Contains(x.EntityId) && (x.Kind == "Session" || x.Kind == "Segment")).ToListAsync(cancellationToken);
        var visible = segments.Any(segment =>
        {
            var session = sessions.FirstOrDefault(x => x.Id == segment.SessionId);
            return session is not null && segment.Revision == session.MediaRevision
                && !trash.Any(entry => entry.EntityId == session.Id || entry.EntityId == segment.Id);
        });
        if (!visible || !MatchesFile(asset))
        {
            throw new InvalidOperationException("The source media was removed or replaced.");
        }
        return asset;
    }

    /// <summary>解析媒体的固定身份，旧档案通过规范化存储相对路径兼容迁移。</summary>
    public static string AssetId(BroadcastSession session, RecordingSegment segment)
    {
        if (!string.IsNullOrEmpty(segment.AssetId))
        {
            return segment.AssetId;
        }
        var relative = RelativePath(session, segment).Replace('\\', '/');
        if (OperatingSystem.IsWindows())
        {
            relative = relative.ToUpperInvariant();
        }
        return "legacy-" + Hash(relative);
    }

    /// <summary>确认原文件仍保有任务输入的已提交字节与文件版本。</summary>
    public bool MatchesFile(MediaAsset asset)
    {
        var file = new FileInfo(MediaPaths.Resolve(paths.RecordsDirectory, asset.SourceRelativePath));
        return file.Exists && file.Length == asset.SourceLength && file.LastWriteTimeUtc.Ticks == asset.SourceWriteTicks;
    }

    /// <summary>返回受存储根约束的源文件路径。</summary>
    public string SourcePath(MediaAsset asset) => MediaPaths.Resolve(paths.RecordsDirectory, asset.SourceRelativePath);

    private async Task BindReferencesAsync(MediaAsset asset, string segmentId, CancellationToken cancellationToken)
    {
        var segments = context.Collection<RecordingSegment>("segments");
        await segments.UpdateManyAsync(x => x.Id == segmentId || x.RootRelativePath == asset.SourceRelativePath,
            Builders<RecordingSegment>.Update.Set(x => x.AssetId, asset.Id), cancellationToken: cancellationToken);
        var directories = new List<string>();
        for (var directory = Path.GetDirectoryName(asset.SourceRelativePath); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            directories.Add(directory);
        }
        var owners = await context.Collection<BroadcastSession>("sessions").Find(x => directories.Contains(x.DirectoryPath)).ToListAsync(cancellationToken);
        foreach (var owner in owners)
        {
            var relative = Path.GetRelativePath(owner.DirectoryPath, asset.SourceRelativePath);
            await segments.UpdateManyAsync(x => x.SessionId == owner.Id && x.RelativePath == relative && x.RootRelativePath == null,
                Builders<RecordingSegment>.Update.Set(x => x.AssetId, asset.Id), cancellationToken: cancellationToken);
        }
    }

    private static string RelativePath(BroadcastSession session, RecordingSegment segment) =>
        segment.RootRelativePath ?? Path.Combine(session.DirectoryPath, segment.RelativePath);

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
