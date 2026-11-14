using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>统一取得场次封面，原图不可用时将录像帧持久化为封面。</summary>
public sealed class SessionCoverService(IMongoContext context, LibraryService library, ArtworkCache artwork,
    IVideoFrameExtractor frames, IAppPaths paths, ILogger<SessionCoverService> logger) : IDisposable
{
    private const int ORIGINAL_COVER_TIMEOUT_SECONDS = 15;
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>复用已保存封面，或从原始地址与当前媒体版本恢复缺失封面。</summary>
    public async Task<string?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var gate = gates[(int)((uint)StringComparer.Ordinal.GetHashCode(sessionId) % gates.Length)];
        await gate.WaitAsync(cancellationToken);
        try
        {
            var detail = await library.GetAsync(sessionId, cancellationToken);
            var session = detail.Session;
            if (SavedCover(session) is { } saved)
            {
                return saved;
            }
            var cached = await GetOriginalAsync(session.CoverUrl, cancellationToken);
            if (cached is not null)
            {
                var type = await File.ReadAllTextAsync(cached + ".type", cancellationToken);
                var extension = type switch { "image/png" => ".png", "image/webp" => ".webp", "image/gif" => ".gif", _ => ".jpg" };
                return await PublishAsync(session, cached, extension, cancellationToken);
            }
            var temporary = Path.Combine(paths.CacheDirectory, "cover-frames", Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var segment in detail.Segments.Where(segment => segment.CommittedBytes > 13 || segment.IsClosed).OrderBy(segment => segment.StartOffsetMs))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var output = Path.Combine(temporary, "frame.jpg");
                    try
                    {
                        var extracted = await frames.TryExtractAsync(new()
                        {
                            SourcePath = MediaPaths.ResolveSegment(paths.RecordsDirectory, session.DirectoryPath, segment),
                            IndexPath = MediaPaths.ResolveSegmentIndex(paths.RecordsDirectory, session.DirectoryPath, segment),
                            CommittedBytes = segment.CommittedBytes,
                            IsClosed = segment.IsClosed,
                            OutputPath = output
                        }, cancellationToken);
                        if (extracted)
                        {
                            return await PublishAsync(session, output, ".jpg", cancellationToken);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        logger.LogWarning(error, "场次 {SessionId} 的片段 {SegmentId} 暂时无法生成封面", session.Id, segment.Id);
                    }
                }
                return null;
            }
            finally
            {
                if (Directory.Exists(temporary))
                {
                    Directory.Delete(temporary, recursive: true);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string?> GetOriginalAsync(string source, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(ORIGINAL_COVER_TIMEOUT_SECONDS));
        try
        {
            var cached = await artwork.GetAsync(source, timeout.Token);
            return cached is not null && new FileInfo(cached).Length > 0 ? cached : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private string? SavedCover(BroadcastSession session)
    {
        if (string.IsNullOrWhiteSpace(session.CoverRelativePath))
        {
            return null;
        }
        var path = MediaPaths.Resolve(paths.RecordsDirectory, session.CoverRelativePath);
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    private async Task<string?> PublishAsync(BroadcastSession snapshot, string source, string extension, CancellationToken cancellationToken)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var collection = context.Collection<BroadcastSession>("sessions");
        var session = await collection.Find(item => item.Id == snapshot.Id && !item.IsTrashed && item.ProjectionReady).FirstOrDefaultAsync(cancellationToken);
        if (session is null || session.DirectoryPath != snapshot.DirectoryPath || session.MediaRevision != snapshot.MediaRevision)
        {
            return null;
        }
        if (SavedCover(session) is { } saved)
        {
            return saved;
        }
        var directory = MediaPaths.Resolve(paths.RecordsDirectory, session.DirectoryPath);
        var destination = MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(session.DirectoryPath, "直播封面" + extension));
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(directory);
        try
        {
            File.Copy(source, temporary);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            session.CoverRelativePath = Path.GetRelativePath(paths.RecordsDirectory, destination);
            await JsonFiles.WriteAsync(Path.Combine(directory, "封面.json"), new { session.CoverRelativePath, session.CoverUrl }, cancellationToken);
            await collection.UpdateOneAsync(item => item.Id == session.Id && !item.IsTrashed,
                Builders<BroadcastSession>.Update.Set(item => item.CoverRelativePath, session.CoverRelativePath), cancellationToken: cancellationToken);
            return destination;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>后台维护与媒体请求停止后释放并发控制资源。</summary>
    public void Dispose()
    {
        foreach (var gate in gates)
        {
            gate.Dispose();
        }
    }
}
