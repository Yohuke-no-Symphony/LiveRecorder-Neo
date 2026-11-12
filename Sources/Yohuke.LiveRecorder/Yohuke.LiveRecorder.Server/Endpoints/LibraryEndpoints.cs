using Yohuke.LiveRecorder.Application.Localization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Bilibili;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Analytics;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Playback;
using Yohuke.LiveRecorder.Infrastructure.Storage;
using Yohuke.LiveRecorder.Server.Contracts;
using Yohuke.LiveRecorder.Server.Localization;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>媒体目录、真实时间轴、私有任务和鉴权文件交付。</summary>
public static class LibraryEndpoints
{
    /// <summary>注册成员媒体端点和管理员录制操作。</summary>
    public static IEndpointRouteBuilder MapLibraryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").RequireAuthorization();
        api.MapGet("/streamers", async (MongoContext context, CancellationToken ct) => Results.Ok((await context.Collection<Streamer>("streamers").Find(FilterDefinition<Streamer>.Empty).SortBy(x => x.Name).ToListAsync(ct)).Select(LocalizedState.ForResponse)));
        api.MapGet("/streamers/{id}/avatar", async (string id, MongoContext context, ArtworkCache artwork, ISettingsStore settingsStore, CancellationToken ct) =>
        {
            var streamer = await context.Collection<Streamer>("streamers").Find(x => x.Id == id).FirstOrDefaultAsync(ct);
            if (streamer?.AvatarRelativePath is { } relative)
            {
                var settings = await settingsStore.GetAsync(ct);
                var path = MediaPaths.Resolve(settings.StoragePath, relative);
                if (File.Exists(path))
                {
                    return Results.File(path, ImageType(path));
                }
            }
            return await ImageAsync(streamer?.AvatarUrl, artwork, ct);
        });
        api.MapGet("/sessions", async (string? streamerId, string? search, string? before, int? take, MongoContext context, CancellationToken ct) =>
        {
            var collection = context.Collection<BroadcastSession>("sessions");
            var filter = Builders<BroadcastSession>.Filter.Where(x => !x.IsTrashed && !x.IsHidden && x.ProjectionReady);
            if (!string.IsNullOrEmpty(streamerId))
            {
                filter &= Builders<BroadcastSession>.Filter.Eq(x => x.StreamerId, streamerId);
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                filter &= Builders<BroadcastSession>.Filter.Regex(x => x.Title, new BsonRegularExpression(Regex.Escape(search[..Math.Min(search.Length, 200)]), "i"));
            }
            if (!string.IsNullOrEmpty(before))
            {
                var previous = await collection.Find(x => x.Id == before).FirstOrDefaultAsync(ct) ?? throw new InfrastructureException(MessageCode.LIBRARY_CURSOR_NOT_FOUND);
                filter &= Builders<BroadcastSession>.Filter.Lt(x => x.CreatedAtUtc, previous.CreatedAtUtc) |
                          (Builders<BroadcastSession>.Filter.Eq(x => x.CreatedAtUtc, previous.CreatedAtUtc) & Builders<BroadcastSession>.Filter.Lt(x => x.Id, previous.Id));
            }
            return Results.Ok((await collection.Find(filter).Sort(Builders<BroadcastSession>.Sort.Descending(x => x.CreatedAtUtc).Descending(x => x.Id)).Limit(Math.Clamp(take ?? 50, 1, 200)).ToListAsync(ct)).Select(LocalizedState.ForResponse));
        });
        api.MapGet("/sessions/{id}", async (string id, LibraryService library, CancellationToken ct) => Results.Ok(LocalizedState.ForResponse(await library.GetAsync(id, ct))));
        api.MapGet("/sessions/{id}/cover", async (string id, LibraryService library, ArtworkCache artwork, ISettingsStore settingsStore, CancellationToken ct) =>
        {
            var session = (await library.GetAsync(id, ct)).Session;
            if (session.CoverRelativePath is { } relative)
            {
                var settings = await settingsStore.GetAsync(ct);
                var path = MediaPaths.Resolve(settings.StoragePath, relative);
                if (File.Exists(path))
                {
                    return Results.File(path, ImageType(path));
                }
            }
            return await ImageAsync(session.CoverUrl, artwork, ct);
        });
        api.MapPost("/sessions/{id}/playback", async (string id, PlaybackRequest request, PlaybackService playback, CancellationToken ct) => Results.Ok(await playback.OpenAsync(id, request.OffsetMs, ct)));
        api.MapGet("/sessions/{id}/events", async (string id, double? fromMs, double? toMs, string? kind, long? cursor, int? limit, LibraryService library, ISettingsStore settingsStore, SessionEventReader reader, CancellationToken ct) =>
        {
            var detail = await library.GetAsync(id, ct);
            var from = fromMs ?? 0;
            var until = toMs ?? from + 60000;
            if (!double.IsFinite(from) || !double.IsFinite(until) || from < 0 || until < from)
            {
                throw new InfrastructureException(MessageCode.LIBRARY_EVENT_RANGE_INVALID);
            }
            var settings = await settingsStore.GetAsync(ct);
            return Results.Ok(LocalizedState.ForResponse(await reader.ReadAsync(detail.Session, settings.StoragePath, from, Math.Min(until, detail.DurationMs), kind, cursor, limit ?? 300, ct)));
        });
        api.MapGet("/sessions/{id}/analytics", async (string id, LibraryService library, ISettingsStore settingsStore, AnalyticsService analytics, CancellationToken ct) =>
        {
            var detail = await library.GetAsync(id, ct);
            var settings = await settingsStore.GetAsync(ct);
            var report = await analytics.GetAsync(detail.Session, settings.StoragePath, ct);
            report.CoverageGaps = Math.Max(report.CoverageGaps, detail.Gaps.Count);
            return Results.Ok(report);
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));
        api.MapGet("/playback/{key}/{generation}/{fileName}", async (string key, string generation, string fileName, PlaybackService playback, CancellationToken ct) =>
        {
            var path = await playback.ResolveAssetAsync(key, generation, fileName, ct);
            var type = Path.GetExtension(path) switch { ".m3u8" => "application/vnd.apple.mpegurl", ".m4s" => "video/iso.segment", ".mp4" => "video/mp4", ".ts" => "video/mp2t", _ => "application/octet-stream" };
            return Results.File(path, type, enableRangeProcessing: true);
        });
        api.MapGet("/jobs", async (HttpContext http, MongoContext context, CancellationToken ct) =>
        {
            var filter = http.User.IsInRole("Admin") ? FilterDefinition<MediaJob>.Empty : Builders<MediaJob>.Filter.Eq(x => x.OwnerId, Owner(http));
            return Results.Ok((await context.Collection<MediaJob>("jobs").Find(filter).SortByDescending(x => x.CreatedAtUtc).Limit(200).ToListAsync(ct)).Select(LocalizedState.ForResponse));
        });
        api.MapPost("/jobs", async (CreateMediaJobRequest request, HttpContext http, MediaJobService jobs, CancellationToken ct) =>
        {
            var job = await jobs.CreateAsync(request, Owner(http), http.User.IsInRole("Admin"), ct);
            return Results.Accepted("/api/jobs/" + job.Id, job);
        });
        api.MapGet("/jobs/{id}", async (string id, HttpContext http, MongoContext context, CancellationToken ct) => Results.Ok(LocalizedState.ForResponse(await GetJobAsync(id, http, context, ct))));
        api.MapPost("/jobs/{id}/cancel", async (string id, HttpContext http, MongoContext context, MediaJobService jobs, CancellationToken ct) =>
        {
            await jobs.CancelAsync(await GetJobAsync(id, http, context, ct), ct);
            return Results.NoContent();
        });
        api.MapPost("/jobs/{id}/retry", async (string id, HttpContext http, MongoContext context, MediaJobService jobs, CancellationToken ct) =>
        {
            await jobs.RetryAsync(await GetJobAsync(id, http, context, ct), ct);
            return Results.NoContent();
        });
        api.MapGet("/jobs/{id}/outputs/{index:int}", async (string id, int index, HttpContext http, MongoContext context, ISettingsStore settingsStore, CancellationToken ct) =>
        {
            var job = await GetJobAsync(id, http, context, ct);
            if (job.State != "Completed" || index < 0 || index >= job.OutputRelativePaths.Count)
            {
                throw new InfrastructureException(MessageCode.LIBRARY_OUTPUT_NOT_READY, 404);
            }
            if (job.Kind == "Clip")
            {
                var clip = await context.Collection<Clip>("clips").Find(x => x.JobId == id && !x.IsTrashed && x.ExpiresAtUtc > DateTime.UtcNow).FirstOrDefaultAsync(ct);
                if (clip is null)
                {
                    throw new InfrastructureException(MessageCode.LIBRARY_CLIP_EXPIRED, 410);
                }
            }
            var settings = await settingsStore.GetAsync(ct);
            var path = MediaPaths.Resolve(settings.StoragePath, job.OutputRelativePaths[index]);
            return Download(path, Path.GetFileName(path));
        });
        api.MapGet("/clips", async (HttpContext http, MongoContext context, CancellationToken ct) =>
        {
            var filter = Builders<Clip>.Filter.Where(x => !x.IsTrashed && x.ExpiresAtUtc > DateTime.UtcNow);
            if (!http.User.IsInRole("Admin"))
            {
                filter &= Builders<Clip>.Filter.Eq(x => x.OwnerId, Owner(http));
            }
            return Results.Ok(await context.Collection<Clip>("clips").Find(filter).SortByDescending(x => x.CreatedAtUtc).ToListAsync(ct));
        });
        api.MapGet("/clips/{id}/download", async (string id, HttpContext http, MongoContext context, ISettingsStore settingsStore, CancellationToken ct) =>
        {
            var clip = await GetClipAsync(id, http, context, ct);
            if (clip.IsTrashed || clip.ExpiresAtUtc <= DateTime.UtcNow)
            {
                throw new InfrastructureException(MessageCode.LIBRARY_CLIP_EXPIRED, 410);
            }
            var settings = await settingsStore.GetAsync(ct);
            return Download(MediaPaths.Resolve(settings.StoragePath, clip.RelativePath), MediaPaths.SafeName(clip.Title) + ".mp4");
        });
        api.MapGet("/clips/{id}/media", async (string id, HttpContext http, MongoContext context, ISettingsStore settingsStore, CancellationToken ct) =>
        {
            var clip = await GetClipAsync(id, http, context, ct);
            if (clip.IsTrashed || clip.ExpiresAtUtc <= DateTime.UtcNow)
            {
                throw new InfrastructureException(MessageCode.LIBRARY_CLIP_EXPIRED, 410);
            }
            var settings = await settingsStore.GetAsync(ct);
            var path = MediaPaths.Resolve(settings.StoragePath, clip.RelativePath);
            if (!File.Exists(path))
            {
                throw new InfrastructureException(MessageCode.LIBRARY_FILE_NOT_FOUND, 410);
            }
            return Results.File(path, "video/mp4", enableRangeProcessing: true);
        });
        api.MapDelete("/clips/{id}", async (string id, HttpContext http, MongoContext context, StorageService storage, CancellationToken ct) =>
        {
            await storage.TrashClipAsync(await GetClipAsync(id, http, context, ct), ct);
            return Results.NoContent();
        });
        api.MapGet("/trash", async (HttpContext http, MongoContext context, CancellationToken ct) =>
        {
            var filter = http.User.IsInRole("Admin") ? FilterDefinition<TrashEntry>.Empty : Builders<TrashEntry>.Filter.Eq(x => x.OwnerId, Owner(http));
            return Results.Ok(await context.Collection<TrashEntry>("trash").Find(filter).SortByDescending(x => x.CreatedAtUtc).ToListAsync(ct));
        });
        api.MapPost("/trash/{id}/restore", async (string id, HttpContext http, MongoContext context, StorageService storage, CancellationToken ct) =>
        {
            await storage.RestoreAsync(await GetTrashAsync(id, http, context, ct), ct);
            return Results.NoContent();
        });
        api.MapDelete("/trash/{id}", async (string id, HttpContext http, MongoContext context, StorageService storage, CancellationToken ct) =>
        {
            await storage.PurgeAsync(await GetTrashAsync(id, http, context, ct), ct);
            return Results.NoContent();
        });
        api.MapGet("/storage", async (StorageService storage, CancellationToken ct) => Results.Ok(new[] { await storage.GetVolumeAsync(ct) }));

        var admin = api.MapGroup("").RequireAuthorization(policy => policy.RequireRole("Admin"));
        admin.MapPost("/sessions/{id}/group", async (string id, SessionGroupRequest request, SessionGroupingService grouping, CancellationToken ct) => Results.Ok(await grouping.GroupAsync(id, request.SessionIds, request.Title, ct)));
        admin.MapPost("/sessions/{id}/split", async (string id, SessionSplitRequest request, SessionGroupingService grouping, CancellationToken ct) => Results.Ok(new { sessions = await grouping.SplitAsync(id, request.BeforeSegmentId, ct) }));
        admin.MapPost("/streamers", async (TargetRequest request, MongoContext context, IBilibiliService bilibili, CancellationToken ct) =>
        {
            if (request.RoomId <= 0)
            {
                throw new InfrastructureException(MessageCode.LIBRARY_ROOM_ID_REQUIRED);
            }
            var room = await bilibili.GetRoomAsync(request.RoomId, request.AccountId, ct);
            var previous = await context.Collection<Streamer>("streamers").Find(x => x.RoomId == room.RoomId).FirstOrDefaultAsync(ct);
            if (previous is { IsMonitored: false })
            {
                var restored = await context.Collection<Streamer>("streamers").FindOneAndUpdateAsync(x => x.Id == previous.Id,
                    Builders<Streamer>.Update.Set(x => x.IsMonitored, true).Set(x => x.Enabled, request.Enabled).Set(x => x.AccountId, request.AccountId),
                    new FindOneAndUpdateOptions<Streamer> { ReturnDocument = ReturnDocument.After }, ct);
                return Results.Ok(restored);
            }
            var target = new Streamer { RoomId = room.RoomId, Uid = room.Uid, Name = room.Name, AvatarUrl = room.AvatarUrl, AccountId = request.AccountId, Enabled = request.Enabled, DirectoryName = MediaPaths.SafeName(room.Name) + "_" + room.Uid };
            try
            {
                await context.Collection<Streamer>("streamers").InsertOneAsync(target, cancellationToken: ct);
            }
            catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                throw new InfrastructureException(MessageCode.LIBRARY_ROOM_ALREADY_EXISTS, 409);
            }
            return Results.Ok(target);
        });
        admin.MapPut("/streamers/{id}", async (string id, Streamer request, MongoContext context, CancellationToken ct) =>
        {
            if (request.Quality is <= 0 || request.SegmentBytes is < 0)
            {
                throw new InfrastructureException(MessageCode.LIBRARY_RECORDING_PARAMETERS_INVALID);
            }
            var result = await context.Collection<Streamer>("streamers").FindOneAndUpdateAsync(x => x.Id == id, Builders<Streamer>.Update.Set(x => x.Enabled, request.Enabled).Set(x => x.AccountId, request.AccountId).Set(x => x.Quality, request.Quality).Set(x => x.RecordDanmu, request.RecordDanmu).Set(x => x.SegmentBytes, request.SegmentBytes), new FindOneAndUpdateOptions<Streamer> { ReturnDocument = ReturnDocument.After }, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
        admin.MapDelete("/streamers/{id}", async (string id, MongoContext context, CancellationToken ct) =>
        {
            await context.Collection<Streamer>("streamers").UpdateOneAsync(x => x.Id == id,
                Builders<Streamer>.Update.Set(x => x.Enabled, false).Set(x => x.IsMonitored, false), cancellationToken: ct);
            return Results.NoContent();
        });
        return endpoints;
    }

    private static string Owner(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static void RequireOwner(HttpContext http, string owner)
    {
        if (owner != Owner(http) && !http.User.IsInRole("Admin"))
        {
            throw new InfrastructureException(MessageCode.LIBRARY_ACCESS_DENIED, 403);
        }
    }

    private static async Task<MediaJob> GetJobAsync(string id, HttpContext http, MongoContext context, CancellationToken ct)
    {
        var job = await context.Collection<MediaJob>("jobs").Find(x => x.Id == id).FirstOrDefaultAsync(ct) ?? throw new InfrastructureException(MessageCode.JOB_NOT_FOUND, 404);
        RequireOwner(http, job.OwnerId);
        return job;
    }

    private static async Task<Clip> GetClipAsync(string id, HttpContext http, MongoContext context, CancellationToken ct)
    {
        var clip = await context.Collection<Clip>("clips").Find(x => x.Id == id).FirstOrDefaultAsync(ct) ?? throw new InfrastructureException(MessageCode.LIBRARY_CLIP_NOT_FOUND, 404);
        RequireOwner(http, clip.OwnerId);
        return clip;
    }

    private static async Task<TrashEntry> GetTrashAsync(string id, HttpContext http, MongoContext context, CancellationToken ct)
    {
        var item = await context.Collection<TrashEntry>("trash").Find(x => x.Id == id).FirstOrDefaultAsync(ct) ?? throw new InfrastructureException(MessageCode.LIBRARY_TRASH_NOT_FOUND, 404);
        RequireOwner(http, item.OwnerId);
        return item;
    }

    private static IResult Download(string path, string name)
    {
        if (!File.Exists(path))
        {
            throw new InfrastructureException(MessageCode.LIBRARY_FILE_NOT_FOUND, 410);
        }
        return Results.File(path, Path.GetExtension(path) == ".flv" ? "video/x-flv" : "video/mp4", name, enableRangeProcessing: true);
    }

    private static async Task<IResult> ImageAsync(string? source, ArtworkCache artwork, CancellationToken ct)
    {
        var path = source is null ? null : await artwork.GetAsync(source, ct);
        return path is null ? Results.NotFound() : Results.File(path, File.Exists(path + ".type") ? await File.ReadAllTextAsync(path + ".type", ct) : "image/jpeg");
    }

    private static string ImageType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/jpeg"
    };
}
