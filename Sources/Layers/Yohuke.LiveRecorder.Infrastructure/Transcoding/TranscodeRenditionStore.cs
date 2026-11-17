using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

internal sealed class TranscodeRenditionStore(IMongoContext context)
{
    internal async Task PublishAsync(PlaybackRendition rendition, CancellationToken cancellationToken)
    {
        var files = context.Collection<PlaybackRenditionFile>("renditionFiles");
        foreach (var batch in rendition.Artifacts.Chunk(1000))
        {
            var writes = batch.Select(artifact => new ReplaceOneModel<PlaybackRenditionFile>(
                Builders<PlaybackRenditionFile>.Filter.Eq(x => x.Id, FileId(rendition.Id, artifact.Name)), new()
                {
                    Id = FileId(rendition.Id, artifact.Name), AssetId = rendition.AssetId,
                    RenditionId = rendition.Id, Name = artifact.Name, Length = artifact.Length,
                    Sha256 = artifact.Sha256, WriteTicks = artifact.WriteTicks
                }) { IsUpsert = true });
            await files.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken);
        }
        var summary = new PlaybackRendition
        {
            Id = rendition.Id, CreatedAtUtc = rendition.CreatedAtUtc,
            AssetId = rendition.AssetId, SourceVersion = rendition.SourceVersion,
            ProfileId = rendition.ProfileId, ProfileName = rendition.ProfileName, RecipeHash = rendition.RecipeHash,
            RelativeDirectory = rendition.RelativeDirectory, State = rendition.State,
            JobId = rendition.JobId, Attempt = rendition.Attempt,
            DurationMs = rendition.DurationMs, SourceStartMs = rendition.SourceStartMs,
            PlaybackTimestampOffsetMs = rendition.PlaybackTimestampOffsetMs,
            MediaEndTimestampMs = rendition.MediaEndTimestampMs,
            Width = rendition.Width, Height = rendition.Height, FrameRate = rendition.FrameRate, Bytes = rendition.Bytes
        };
        await context.Collection<PlaybackRendition>("playbackRenditions").ReplaceOneAsync(x => x.Id == summary.Id,
            summary, new ReplaceOptions { IsUpsert = true }, cancellationToken);
    }

    internal static string FileId(string renditionId, string fileName) => TranscodeAssetRegistry.Hash(renditionId + ":" + fileName);
}
