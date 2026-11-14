using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>在采集之外持久化主播头像和开播封面快照。</summary>
public sealed class ArtworkMaintenanceWorker(IMongoContext context, IBootstrapStore bootstrap, ArtworkCache artwork,
    SessionCoverService covers, ILogger<ArtworkMaintenanceWorker> logger, IAppPaths paths) : BackgroundService
{
    private const int COVER_BATCH_SIZE = 20;
    private string? coverCursor;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (bootstrap.IsInstalled)
                {
                    var filter = Builders<BroadcastSession>.Filter.Where(x => !x.IsTrashed && !x.IsHidden && x.ProjectionReady && (x.CoverRelativePath == null || x.CoverRelativePath == ""));
                    if (coverCursor is not null)
                    {
                        filter &= Builders<BroadcastSession>.Filter.Gt(x => x.Id, coverCursor);
                    }
                    var sessions = await context.Collection<BroadcastSession>("sessions").Find(filter).SortBy(x => x.Id).Limit(COVER_BATCH_SIZE).ToListAsync(stoppingToken);
                    coverCursor = sessions.Count == COVER_BATCH_SIZE ? sessions[^1].Id : null;
                    foreach (var session in sessions)
                    {
                        try
                        {
                            await covers.GetAsync(session.Id, stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception error)
                        {
                            logger.LogWarning(error, "场次 {SessionId} 的封面将在稍后重试", session.Id);
                        }
                    }
                    var streamers = await context.Collection<Streamer>("streamers").Find(x => x.AvatarRelativePath == null && x.AvatarUrl != "" && x.DirectoryName != "").Limit(50).ToListAsync(stoppingToken);
                    foreach (var streamer in streamers)
                    {
                        var saved = await SaveAsync(streamer.AvatarUrl, Path.Combine(MediaPaths.Resolve(paths.RecordsDirectory, streamer.DirectoryName), "头像"), stoppingToken);
                        if (saved is not null)
                        {
                            await context.Collection<Streamer>("streamers").UpdateOneAsync(x => x.Id == streamer.Id && x.AvatarUrl == streamer.AvatarUrl, Builders<Streamer>.Update.Set(x => x.AvatarRelativePath, Path.GetRelativePath(paths.RecordsDirectory, saved)), cancellationToken: stoppingToken);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning("封面快照将在稍后重试：{Message}", error.Message);
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<string?> SaveAsync(string url, string destinationStem, CancellationToken cancellationToken)
    {
        var cached = await artwork.GetAsync(url, cancellationToken);
        if (cached is null)
        {
            return null;
        }
        var type = await File.ReadAllTextAsync(cached + ".type", cancellationToken);
        var extension = type switch { "image/png" => ".png", "image/webp" => ".webp", "image/gif" => ".gif", _ => ".jpg" };
        Directory.CreateDirectory(Path.GetDirectoryName(destinationStem)!);
        var destination = destinationStem + extension;
        var temporary = destination + ".tmp";
        File.Copy(cached, temporary, true);
        File.Move(temporary, destination, true);
        return destination;
    }
}
