using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>将物理资产的已发布档位映射到当前可见的场次分段。</summary>
public sealed class TranscodeRenditionCatalog(IMongoContext context, LibraryService library,
    TranscodeAssetRegistry assets, IAppPaths paths)
{
    /// <summary>只返回当前源版本的已完成产物，同时保留同一档位的历史配方供回退。</summary>
    public async Task<IReadOnlyList<PlaybackRendition>> GetAsync(string sessionId, string segmentId,
        CancellationToken cancellationToken)
    {
        var detail = await library.GetAsync(sessionId, cancellationToken);
        var segment = detail.Segments.FirstOrDefault(x => x.Id == segmentId);
        if (segment is null || !segment.IsClosed || segment.CommittedBytes <= 0 || segment.DurationMs <= 0)
        {
            return [];
        }
        MediaAsset asset;
        try
        {
            asset = assets.Describe(detail.Session, segment);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FileNotFoundException)
        {
            return [];
        }
        var renditions = await context.Collection<PlaybackRendition>("playbackRenditions")
            .Find(x => x.AssetId == asset.Id && x.SourceVersion == asset.SourceVersion && x.State == "Ready")
            .Project<PlaybackRendition>(Builders<PlaybackRendition>.Projection.Exclude(x => x.Artifacts))
            .SortByDescending(x => x.CreatedAtUtc).ToListAsync(cancellationToken);
        return renditions.Where(x => File.Exists(MediaPaths.Resolve(paths.RecordsDirectory,
            Path.Combine(x.RelativeDirectory, "index.m3u8")))).ToArray();
    }

    /// <summary>每次交付前重新验证逻辑引用、物理版本和已发布文件内容身份。</summary>
    public async Task<RenditionAsset> ResolveAsync(string sessionId, string segmentId, string renditionId,
        string fileName, CancellationToken cancellationToken)
    {
        TranscodeArtifactPolicy.ValidateName(fileName);
        var rendition = (await GetAsync(sessionId, segmentId, cancellationToken)).FirstOrDefault(x => x.Id == renditionId)
            ?? throw new InfrastructureException("转码播放版本已不可用。", 410);
        var artifactId = TranscodeRenditionStore.FileId(rendition.Id, fileName);
        var artifact = await context.Collection<PlaybackRenditionFile>("renditionFiles").Find(x => x.Id == artifactId).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InfrastructureException("转码播放文件不存在。", 404);
        var path = MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(rendition.RelativeDirectory, fileName));
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != artifact.Length || file.LastWriteTimeUtc.Ticks != artifact.WriteTicks)
        {
            throw new InfrastructureException("转码播放文件已发生变化。", 410);
        }
        return new(path, artifact.Sha256, artifact.Length);
    }
}
