using Yohuke.LiveRecorder.Application.Localization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Bilibili;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Analytics;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Playback;
using Yohuke.LiveRecorder.Infrastructure.Storage;
using Yohuke.LiveRecorder.Server.Bandwidth;
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
        var viewing = api.MapGroup("").RequireAuthorization(UserPermissionNames.VIEW_RECORDINGS);
        var downloading = viewing.MapGroup("").RequireAuthorization(UserPermissionNames.DOWNLOAD_RECORDINGS);
        viewing.MapGet("/streamers", async (LibraryService library, CancellationToken ct) => Results.Ok((await library.GetStreamersAsync(ct)).Select(LocalizedState.ForResponse)));
        viewing.MapGet("/streamers/{id}/avatar", async (string id, IMongoContext context, ArtworkCache artwork, IAppPaths paths, CancellationToken ct) =>
        {
            var streamer = await context.Collection<Streamer>("streamers").Find(x => x.Id == id).FirstOrDefaultAsync(ct);
            if (streamer?.AvatarRelativePath is { } relative)
            {
                var path = MediaPaths.Resolve(paths.RecordsDirectory, relative);
                if (File.Exists(path))
                {
                    return Results.File(path, ImageType(path));
                }
            }
            return await ImageAsync(streamer?.AvatarUrl, artwork, ct);
        });
        viewing.MapGet("/sessions", async (string? streamerId, string? search, string? before, int? take, IMongoContext context, ISettingsStore settingsStore, CancellationToken ct) =>
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
            var settings = await settingsStore.GetAsync(ct);
            var sessions = await collection.Find(filter)
                .Sort(Builders<BroadcastSession>.Sort.Descending(x => x.CreatedAtUtc).Descending(x => x.Id))
                .Limit(Math.Clamp(take ?? 50, 1, 200)).ToListAsync(ct);
            return Results.Ok(sessions.Select(session => new SessionResponse(LocalizedState.ForResponse(session),
                SessionRetentionPolicy.GetExpiresAtUtc(session, settings))));
        });
        viewing.MapGet("/sessions/{id}", async (string id, LibraryService library, ISettingsStore settingsStore, CancellationToken ct) =>
        {
            var detail = LocalizedState.ForResponse(await library.GetAsync(id, ct));
            var settings = await settingsStore.GetAsync(ct);
            return Results.Ok(new SessionDetailResponse(detail,
                SessionRetentionPolicy.GetExpiresAtUtc(detail.Session, settings)));
        });
        downloading.MapGet("/sessions/{id}/segments/{segmentId}/download", async (string id, string segmentId, LibraryService library, IAppPaths paths, CancellationToken ct) =>
        {
            var detail = await library.GetAsync(id, ct);
            var segment = detail.Segments.FirstOrDefault(item => item.Id == segmentId)
                          ?? throw new InfrastructureException(MessageCode.LIBRARY_SEGMENT_NOT_FOUND, 404);
            if (!segment.IsClosed || segment.DurationMs <= 0 || segment.CommittedBytes < 13)
            {
                throw new InfrastructureException(MessageCode.LIBRARY_SEGMENT_NOT_READY, 409);
            }
            var path = MediaPaths.ResolveSegment(paths.RecordsDirectory, detail.Session.DirectoryPath, segment);
            return Download(path, Path.GetFileName(path));
        });
        downloading.MapGet("/sessions/{id}/events/download", async (string id, bool? translationsOnly, HttpContext http,
            LibraryService library, IAppPaths paths, ISettingsStore settingsStore, SessionEventExportService exporter, CancellationToken ct) =>
        {
            var detail = await library.GetAsync(id, ct);
            var pattern = translationsOnly == true
                ? TranslationDanmuPattern.Create(TranslationDanmuPattern.Resolve(detail.Session, await settingsStore.GetAsync(ct)))
                : null;
            try
            {
                var (content, fileName) = await exporter.ExportAsync(detail.Session, paths.RecordsDirectory, pattern, ct);
                http.Response.Headers.CacheControl = "private, no-store";
                return Results.File(content, "text/plain; charset=utf-8", fileName);
            }
            catch (RegexMatchTimeoutException)
            {
                throw new InfrastructureException(MessageCode.TRANSLATION_DANMU_PATTERN_TIMEOUT);
            }
        });
        viewing.MapGet("/sessions/{id}/cover", async (string id, SessionCoverService covers, CancellationToken ct) =>
        {
            var path = await covers.GetAsync(id, ct);
            return path is null ? Results.NotFound() : Results.File(path, ImageType(path));
        });
        viewing.MapPost("/sessions/{id}/playback", async (string id, PlaybackRequest request, RenditionPlaybackService playback, CancellationToken ct) =>
        {
            var window = await playback.OpenAsync(id, request.OffsetMs, request.QualityId, request.Prefetch, ct);
            return window is null ? Results.NoContent() : Results.Ok(window);
        });
        viewing.MapMethods("/sessions/{id}/renditions/{segmentId}/{renditionId}/{fileName}", [HttpMethods.Get, HttpMethods.Head],
            async (string id, string segmentId, string renditionId, string fileName, Yohuke.LiveRecorder.Infrastructure.Transcoding.TranscodingCoordinator transcoding, CancellationToken ct) =>
            {
                var asset = await transcoding.ResolveRenditionAssetAsync(id, segmentId, renditionId, fileName, ct);
                return new MediaFileResult(asset.Path, fileName == "index.m3u8" ? "application/vnd.apple.mpegurl" : "video/mp4",
                    MediaTrafficKind.Playback, contentHash: asset.ContentHash);
            });
        viewing.MapMethods("/sessions/{id}/playback/{segmentId}/{sourceKey}/index.m3u8", [HttpMethods.Get, HttpMethods.Head],
            async (string id, string segmentId, string sourceKey, ContinuousPlaybackService continuous, CancellationToken ct) =>
            {
                var asset = await continuous.ResolvePlaylistAsync(id, segmentId, sourceKey, ct);
                return new MediaFileResult(asset.Path, "application/vnd.apple.mpegurl", MediaTrafficKind.Playback, contentHash: asset.ContentHash);
            });
        viewing.MapMethods("/sessions/{id}/playback/{segmentId}/{sourceKey}/{sequence:int}/{fileName}", [HttpMethods.Get, HttpMethods.Head],
            async (string id, string segmentId, string sourceKey, int sequence, string fileName, ContinuousPlaybackService continuous, CancellationToken ct) =>
            {
                var asset = await continuous.ResolveFragmentAsync(id, segmentId, sourceKey, sequence, fileName, ct);
                return new MediaFileResult(asset.Path, "video/mp4", MediaTrafficKind.Playback, contentHash: asset.ContentHash);
            });
        viewing.MapGet("/sessions/{id}/events/playback", async (string id, double offsetMs, string? kind, int? limit, LibraryService library, IAppPaths paths, SessionEventReader reader, CancellationToken ct) =>
        {
            var detail = await library.GetAsync(id, ct);
            return Results.Ok(LocalizedState.ForResponse(await reader.ReadPlaybackWindowAsync(detail, paths.RecordsDirectory, offsetMs, kind, limit ?? 500, ct)));
        });
        viewing.MapGet("/sessions/{id}/events", async (string id, double? fromMs, double? toMs, string? kind, long? cursor, int? limit, bool? translationsOnly, LibraryService library, IAppPaths paths, SessionEventReader reader, ISettingsStore settingsStore, CancellationToken ct) =>
        {
            var detail = await library.GetAsync(id, ct);
            var from = fromMs ?? 0;
            var until = toMs ?? (translationsOnly == true ? detail.DurationMs : from + 60000);
            if (!double.IsFinite(from) || !double.IsFinite(until) || from < 0 || until < from)
            {
                throw new InfrastructureException(MessageCode.LIBRARY_EVENT_RANGE_INVALID);
            }
            Regex? textPattern = null;
            if (translationsOnly == true)
            {
                var pattern = TranslationDanmuPattern.Resolve(detail.Session, await settingsStore.GetAsync(ct));
                textPattern = TranslationDanmuPattern.Create(pattern);
                kind = "Danmu";
            }
            try
            {
                return Results.Ok(LocalizedState.ForResponse(await reader.ReadAsync(detail, paths.RecordsDirectory, from, Math.Min(until, detail.DurationMs), kind, cursor, limit ?? 300, textPattern, ct)));
            }
            catch (RegexMatchTimeoutException)
            {
                throw new InfrastructureException(MessageCode.TRANSLATION_DANMU_PATTERN_TIMEOUT);
            }
        });
        viewing.MapGet("/sessions/{id}/analytics/contributions", async (string id, string kind, string? view, double? fromMs, double? toMs, int? cursor, int? limit, string? snapshotId, LibraryService library, IAppPaths paths, AnalyticsContributionService contributions, CancellationToken ct) =>
        {
            var detail = await library.GetAsync(id, ct);
            return Results.Ok(await contributions.GetAsync(detail, paths.RecordsDirectory, kind, view ?? "events", fromMs, toMs, cursor ?? 0, limit ?? 50, snapshotId, ct));
        }).RequireAuthorization(UserPermissionNames.VIEW_ANALYTICS);
        viewing.MapGet("/sessions/{id}/analytics", async (string id, double? fromMs, double? toMs, LibraryService library, IAppPaths paths, AnalyticsService analytics, ISettingsStore settingsStore, CancellationToken ct) =>
        {
            var detail = await library.GetAsync(id, ct);
            if (fromMs is { } from && (!double.IsFinite(from) || from < 0 || from >= detail.DurationMs)
                || toMs is { } to && (!double.IsFinite(to) || to <= (fromMs ?? 0) || to > detail.DurationMs))
            {
                throw new InfrastructureException(MessageCode.LIBRARY_EVENT_RANGE_INVALID);
            }
            var pattern = TranslationDanmuPattern.Resolve(detail.Session, await settingsStore.GetAsync(ct));
            try
            {
                var report = await analytics.GetAsync(detail.Session, paths.RecordsDirectory, detail.DurationMs, pattern, fromMs, toMs, ct);
                report.CoverageGaps = Math.Max(report.CoverageGaps, detail.Gaps.Count(gap => gap.StartMs < (toMs ?? detail.DurationMs) && gap.EndMs > (fromMs ?? 0)));
                return Results.Ok(report);
            }
            catch (RegexMatchTimeoutException)
            {
                throw new InfrastructureException(MessageCode.TRANSLATION_DANMU_PATTERN_TIMEOUT);
            }
        }).RequireAuthorization(UserPermissionNames.VIEW_ANALYTICS);
        viewing.MapMethods("/playback/{key}/{generation}/{fileName}", [HttpMethods.Get, HttpMethods.Head], async (string key, string generation, string fileName, PlaybackService playback, CancellationToken ct) =>
        {
            var asset = await playback.ResolveAssetAsync(key, generation, fileName, ct);
            var type = Path.GetExtension(asset.Path) switch { ".m3u8" => "application/vnd.apple.mpegurl", ".m4s" => "video/iso.segment", ".mp4" => "video/mp4", ".ts" => "video/mp2t", _ => "application/octet-stream" };
            return new MediaFileResult(asset.Path, type, MediaTrafficKind.Playback, contentHash: asset.ContentHash);
        });
        api.MapGet("/jobs", async (HttpContext http, IMongoContext context, CancellationToken ct) =>
        {
            var filter = http.User.IsInRole("Admin") ? FilterDefinition<MediaJob>.Empty : Builders<MediaJob>.Filter.Eq(x => x.OwnerId, Owner(http));
            return Results.Ok((await context.Collection<MediaJob>("jobs").Find(filter).SortByDescending(x => x.CreatedAtUtc).Limit(200).ToListAsync(ct)).Select(LocalizedState.ForResponse));
        });
        api.MapPost("/jobs", async (CreateMediaJobRequest request, HttpContext http, MediaJobService jobs, CancellationToken ct) =>
        {
            RequirePermission(http, UserPermissionNames.VIEW_RECORDINGS);
            if (request.Kind == "Clip")
            {
                RequirePermission(http, UserPermissionNames.CREATE_CLIPS);
            }
            var job = await jobs.CreateAsync(request, Owner(http), http.User.IsInRole("Admin"), ct,
                TaskProvenance.User(Owner(http), http.User.Identity?.Name ?? ""));
            return Results.Accepted("/api/jobs/" + job.Id, job);
        });
        api.MapGet("/jobs/{id}", async (string id, HttpContext http, IMongoContext context, CancellationToken ct) => Results.Ok(LocalizedState.ForResponse(await GetJobAsync(id, http, context, ct))));
        api.MapPost("/jobs/{id}/cancel", async (string id, HttpContext http, IMongoContext context, MediaJobService jobs, CancellationToken ct) =>
        {
            await jobs.CancelAsync(await GetJobAsync(id, http, context, ct), ct);
            return Results.NoContent();
        });
        api.MapPost("/jobs/{id}/retry", async (string id, HttpContext http, IMongoContext context, MediaJobService jobs, CancellationToken ct) =>
        {
            var job = await GetJobAsync(id, http, context, ct);
            RequirePermission(http, UserPermissionNames.VIEW_RECORDINGS);
            if (job.Kind == "Clip")
            {
                RequirePermission(http, UserPermissionNames.CREATE_CLIPS);
            }
            else if (!http.User.IsInRole("Admin"))
            {
                throw new InfrastructureException(MessageCode.LIBRARY_ACCESS_DENIED, 403);
            }
            await jobs.RetryAsync(job, ct);
            return Results.NoContent();
        });
        downloading.MapGet("/jobs/{id}/outputs/{index:int}", async (string id, int index, HttpContext http, IMongoContext context, IAppPaths paths, CancellationToken ct) =>
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
            var path = MediaPaths.Resolve(paths.RecordsDirectory, job.OutputRelativePaths[index]);
            return Download(path, Path.GetFileName(path));
        });
        api.MapGet("/storage", async (bool? refresh, StorageUsageService storage, CancellationToken ct) => Results.Ok(await storage.GetVolumesAsync(refresh ?? false, ct)));

        var recordings = api.MapGroup("").RequireAuthorization(UserPermissionNames.MANAGE_RECORDINGS);
        recordings.MapDelete("/sessions/{id}", async (string id, HttpContext http, SessionTrashService sessionTrash, CancellationToken ct) =>
        {
            await sessionTrash.TrashAsync(id, Owner(http), ct);
            return Results.NoContent();
        });
        recordings.MapPost("/sessions/trash", async (SessionTrashRequest request, HttpContext http, SessionTrashService sessionTrash, CancellationToken ct) =>
        {
            if (request.SessionIds is null || request.SessionIds.Count is < 1 or > 200 || request.SessionIds.Any(string.IsNullOrWhiteSpace))
            {
                throw new InfrastructureException(MessageCode.LIBRARY_DELETE_COUNT_INVALID);
            }
            var results = new List<SessionTrashResult>();
            foreach (var id in request.SessionIds.Distinct(StringComparer.Ordinal))
            {
                try
                {
                    await sessionTrash.TrashAsync(id, Owner(http), ct);
                    results.Add(new() { SessionId = id, Success = true });
                }
                catch (InfrastructureException error)
                {
                    results.Add(new() { SessionId = id, Error = LocalizedText.Format(error.Code, error.Message, error.Arguments) });
                }
                catch (IOException)
                {
                    results.Add(new() { SessionId = id, Error = LocalizedText.Get(MessageCode.LIBRARY_DELETE_FILES_FAILED) });
                }
                catch (UnauthorizedAccessException)
                {
                    results.Add(new() { SessionId = id, Error = LocalizedText.Get(MessageCode.LIBRARY_DELETE_FILES_FAILED) });
                }
            }
            return Results.Ok(results);
        });
        recordings.MapPut("/sessions/{id}/retention", async (string id, SessionRetentionRequest request, SessionTrashService sessionTrash, ISettingsStore settingsStore, CancellationToken ct) =>
        {
            var session = LocalizedState.ForResponse(await sessionTrash.SetPermanentAsync(id, request.IsPermanent, ct));
            var settings = await settingsStore.GetAsync(ct);
            return Results.Ok(new SessionResponse(session, SessionRetentionPolicy.GetExpiresAtUtc(session, settings)));
        });

        var admin = api.MapGroup("").RequireAuthorization(policy => policy.RequireRole("Admin"));
        admin.MapGet("/storage/details", async (bool? refresh, StorageUsageService storage, StorageUsageMetadataService metadata, CancellationToken ct) =>
            Results.Ok(await metadata.EnrichAsync(await storage.GetDetailsAsync(refresh ?? false, ct), ct)));
        recordings.MapPost("/sessions/{id}/group", async (string id, SessionGroupRequest request, HttpContext http, SessionGroupingService grouping, CancellationToken ct) =>
        {
            if (request.Configuration is not null && !http.User.IsInRole("Admin") && !http.User.HasClaim(UserPermissionNames.CLAIM_TYPE, PermissionIds.CONFIGURE_RECORDINGS))
            {
                return Results.Forbid();
            }
            return Results.Ok(await grouping.GroupAsync(id, request.SessionIds, request.Title, request.Configuration, ct));
        });
        recordings.MapPost("/sessions/{id}/split", async (string id, SessionSplitRequest request, SessionGroupingService grouping, CancellationToken ct) => Results.Ok(new { sessions = await grouping.SplitAsync(id, request.BeforeSegmentId, ct) }));
        admin.MapPost("/streamers", async (TargetRequest request, IMongoContext context, IBilibiliService bilibili, CancellationToken ct) =>
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
        admin.MapPut("/streamers/{id}", async (string id, Streamer request, IMongoContext context, CancellationToken ct) =>
        {
            if (request.Quality is <= 0 || request.SegmentBytes is < 0)
            {
                throw new InfrastructureException(MessageCode.LIBRARY_RECORDING_PARAMETERS_INVALID);
            }
            var result = await context.Collection<Streamer>("streamers").FindOneAndUpdateAsync(x => x.Id == id, Builders<Streamer>.Update.Set(x => x.Enabled, request.Enabled).Set(x => x.AccountId, request.AccountId).Set(x => x.Quality, request.Quality).Set(x => x.RecordDanmu, request.RecordDanmu).Set(x => x.SegmentBytes, request.SegmentBytes), new FindOneAndUpdateOptions<Streamer> { ReturnDocument = ReturnDocument.After }, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
        admin.MapDelete("/streamers/{id}", async (string id, IMongoContext context, CancellationToken ct) =>
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

    private static async Task<MediaJob> GetJobAsync(string id, HttpContext http, IMongoContext context, CancellationToken ct)
    {
        var job = await context.Collection<MediaJob>("jobs").Find(x => x.Id == id).FirstOrDefaultAsync(ct) ?? throw new InfrastructureException(MessageCode.JOB_NOT_FOUND, 404);
        RequireOwner(http, job.OwnerId);
        return job;
    }

    private static void RequirePermission(HttpContext http, string permission)
    {
        if (!UserPermissionNames.IsGranted(http.User, permission))
        {
            throw new InfrastructureException(MessageCode.LIBRARY_ACCESS_DENIED, 403);
        }
    }

    private static IResult Download(string path, string name)
    {
        if (!File.Exists(path))
        {
            throw new InfrastructureException(MessageCode.LIBRARY_FILE_NOT_FOUND, 410);
        }
        return new MediaFileResult(path, Path.GetExtension(path) == ".flv" ? "video/x-flv" : "video/mp4", MediaTrafficKind.Download, name);
    }

    private static async Task<IResult> ImageAsync(string? source, ArtworkCache artwork, CancellationToken ct)
    {
        var image = source is null ? null : await artwork.GetAsync(source, ct);
        return image is null ? Results.NotFound() : new ArtworkFileResult(image);
    }

    private static string ImageType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/jpeg"
    };
}
