using System.Security.Claims;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Storage;
using Yohuke.LiveRecorder.Server.Bandwidth;
using Yohuke.LiveRecorder.Server.Contracts;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>个人切片、全站切片管理和切片媒体交付。</summary>
public static class ClipLibraryEndpoints
{
    private const int DEFAULT_PAGE_SIZE = 25;
    private const int MAX_PAGE_SIZE = 100;
    private const int MAX_TRASH_ENTRIES = 200;

    /// <summary>注册个人切片和需要切片管理权限的全站接口。</summary>
    public static IEndpointRouteBuilder MapClipLibraryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var clips = endpoints.MapGroup("/api/clips").RequireAuthorization();
        clips.MapGet("", async (HttpContext http, IMongoContext context, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "private, no-store";
            var filter = ActiveClips() & Builders<Clip>.Filter.Eq(clip => clip.OwnerId, Owner(http));
            var items = await context.Collection<Clip>("clips").Find(filter)
                .SortByDescending(clip => clip.CreatedAtUtc).ThenByDescending(clip => clip.Id).ToListAsync(ct);
            return Results.Ok(items);
        });
        clips.MapGet("/{id}/media", async (string id, HttpContext http, IMongoContext context, IAppPaths paths, CancellationToken ct) =>
        {
            var clip = await GetAccessibleClipAsync(id, http, context, ct);
            return new MediaFileResult(ResolveAvailableFile(clip, paths), "video/mp4", MediaTrafficKind.Playback);
        }).RequireAuthorization(UserPermissionNames.VIEW_RECORDINGS);
        clips.MapGet("/{id}/cover", async (string id, HttpContext http, IMongoContext context, IAppPaths paths,
            ClipCoverService covers, SessionCoverService sessionCovers, CancellationToken ct) =>
        {
            var clip = await GetAccessibleClipAsync(id, http, context, ct);
            return await CoverAsync(clip, ResolveAvailableFile(clip, paths), http, covers, sessionCovers, ct);
        }).RequireAuthorization(UserPermissionNames.VIEW_RECORDINGS);
        clips.MapGet("/{id}/download", async (string id, HttpContext http, IMongoContext context, IAppPaths paths, CancellationToken ct) =>
        {
            var clip = await GetAccessibleClipAsync(id, http, context, ct);
            return new MediaFileResult(ResolveAvailableFile(clip, paths), "video/mp4", MediaTrafficKind.Download,
                MediaPaths.SafeName(clip.Title) + ".mp4");
        }).RequireAuthorization(UserPermissionNames.VIEW_RECORDINGS, UserPermissionNames.DOWNLOAD_RECORDINGS);
        clips.MapDelete("/{id}", async (string id, HttpContext http, IMongoContext context, StorageService storage, CancellationToken ct) =>
        {
            await storage.TrashClipAsync(await GetAccessibleClipAsync(id, http, context, ct), ct);
            return Results.NoContent();
        });

        var management = endpoints.MapGroup("/api/admin/clips").RequireAuthorization(UserPermissionNames.MANAGE_CLIPS);
        management.MapGet("", ListManagedAsync);
        management.MapPost("/trash", TrashBatchAsync);
        return endpoints;
    }

    internal static async Task<IResult> CoverAsync(Clip clip, string path, HttpContext http, ClipCoverService covers,
        SessionCoverService sessionCovers, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        string? original;
        try
        {
            original = await sessionCovers.GetAsync(clip.SessionId, ct);
        }
        catch (InfrastructureException error) when (error.Code == nameof(MessageCode.LIBRARY_SESSION_NOT_FOUND))
        {
            original = null;
        }
        if (original is not null)
        {
            return LibraryEndpoints.SavedImage(original);
        }
        var image = await covers.GetAsync(clip.Id, path, ct);
        return image is null ? Results.NotFound() : new ArtworkFileResult(image);
    }

    private static async Task<IResult> ListManagedAsync(int? page, int? pageSize, string? sort, string? direction, HttpContext http,
        IMongoContext context, CancellationToken ct)
    {
        var currentPage = page ?? 1;
        var size = pageSize ?? DEFAULT_PAGE_SIZE;
        var skip = ((long)currentPage - 1) * size;
        if (currentPage < 1 || size is < 1 or > MAX_PAGE_SIZE || skip > int.MaxValue ||
            direction is not (null or "asc" or "desc"))
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
        var sortField = sort switch
        {
            null or "createdAtUtc" => nameof(Clip.CreatedAtUtc),
            "expiresAtUtc" => nameof(Clip.ExpiresAtUtc),
            "title" => nameof(Clip.Title),
            "bytes" => nameof(Clip.Bytes),
            "ownerDisplayName" => nameof(ManagedClipResponse.OwnerDisplayName),
            "duration" => "Duration",
            _ => throw new InfrastructureException(MessageCode.REQUEST_INVALID)
        };
        http.Response.Headers.CacheControl = "private, no-store";
        var clips = context.Collection<Clip>("clips");
        var filter = ActiveClips();
        var total = await clips.CountDocumentsAsync(filter, cancellationToken: ct);
        var query = clips.Aggregate(new AggregateOptions { AllowDiskUse = true }).Match(filter);
        var sortByOwner = sortField == nameof(ManagedClipResponse.OwnerDisplayName);
        if (sortByOwner)
        {
            query = IncludeOwners(query);
        }
        if (sortField == "Duration")
        {
            query = query.AppendStage<Clip>(new BsonDocument("$set", new BsonDocument(sortField,
                new BsonDocument("$subtract", new BsonArray { "$EndMs", "$StartMs" }))));
        }
        query = query.Sort(new BsonDocument { { sortField, direction == "asc" ? 1 : -1 }, { "_id", -1 } })
            .Skip((int)skip).Limit(size);
        if (!sortByOwner)
        {
            query = IncludeOwners(query);
        }
        var items = await query.Project<ManagedClipResponse>(new BsonDocument
        {
            { "_id", 1 }, { nameof(Clip.OwnerId), 1 }, { nameof(Clip.SessionId), 1 }, { nameof(Clip.Title), 1 },
            { nameof(Clip.Bytes), 1 }, { nameof(Clip.StartMs), 1 }, { nameof(Clip.EndMs), 1 },
            { nameof(Clip.CreatedAtUtc), 1 }, { nameof(Clip.ExpiresAtUtc), 1 },
            { nameof(ManagedClipResponse.OwnerDisplayName), 1 }, { nameof(ManagedClipResponse.OwnerAvatarUrl), 1 },
            { nameof(ManagedClipResponse.OwnerProfileId), 1 }
        }).ToListAsync(ct);
        return Results.Ok(new ManagedClipPage(items, total));
    }

    private static IAggregateFluent<Clip> IncludeOwners(IAggregateFluent<Clip> query)
    {
        var displayName = new BsonDocument("$ifNull", new BsonArray { new BsonDocument("$first", "$_owner.DisplayName"), "" });
        var hasName = new BsonDocument("$ne", new BsonArray
        {
            new BsonDocument("$trim", new BsonDocument("input", displayName)), ""
        });
        var hasProfile = new BsonDocument("$eq", new BsonArray { new BsonDocument("$first", "$_owner.Status"), "Active" });
        return query.AppendStage<Clip>(new BsonDocument("$lookup", new BsonDocument
        {
            { "from", "users" }, { "localField", nameof(Clip.OwnerId) }, { "foreignField", "_id" }, { "as", "_owner" },
            { "pipeline", new BsonArray { new BsonDocument("$project", new BsonDocument { { "DisplayName", 1 }, { "AvatarUrl", 1 }, { "Status", 1 } }) } }
        })).AppendStage<Clip>(new BsonDocument("$set", new BsonDocument
        {
            { nameof(ManagedClipResponse.OwnerDisplayName), new BsonDocument("$cond", new BsonArray { hasName, displayName, "$OwnerId" }) },
            { nameof(ManagedClipResponse.OwnerAvatarUrl), new BsonDocument("$first", "$_owner.AvatarUrl") },
            { nameof(ManagedClipResponse.OwnerProfileId), new BsonDocument("$cond", new BsonArray { hasProfile, "$OwnerId", BsonNull.Value }) }
        }));
    }

    private static async Task<IResult> TrashBatchAsync(ClipTrashRequest request, IMongoContext context, StorageService storage,
        CancellationToken ct)
    {
        if (request.ClipIds is null || request.ClipIds.Count is < 1 or > MAX_TRASH_ENTRIES ||
            request.ClipIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new InfrastructureException(MessageCode.LIBRARY_CLIP_TRASH_COUNT_INVALID);
        }
        var ids = request.ClipIds.Distinct(StringComparer.Ordinal).ToArray();
        var results = new List<ClipTrashResult>(ids.Length);
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await storage.TrashClipAsync(await GetClipAsync(id, context, ct), ct);
                results.Add(new(id, true));
            }
            catch (InfrastructureException error)
            {
                results.Add(new(id, false, LocalizedText.Format(error.Code, error.Message, error.Arguments)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                results.Add(new(id, false, LocalizedText.Get(MessageCode.LIBRARY_DELETE_FILES_FAILED)));
            }
        }
        return Results.Ok(results);
    }

    private static FilterDefinition<Clip> ActiveClips()
    {
        var now = DateTime.UtcNow;
        return Builders<Clip>.Filter.Where(clip => !clip.IsTrashed && clip.ExpiresAtUtc > now)
            & Builders<Clip>.Filter.Ne(clip => clip.PublicationPending, true);
    }

    private static string Owner(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static async Task<Clip> GetClipAsync(string id, IMongoContext context, CancellationToken ct) =>
        await context.Collection<Clip>("clips").Find(clip => clip.Id == id).FirstOrDefaultAsync(ct)
        ?? throw new InfrastructureException(MessageCode.LIBRARY_CLIP_NOT_FOUND, 404);

    private static async Task<Clip> GetAccessibleClipAsync(string id, HttpContext http, IMongoContext context, CancellationToken ct)
    {
        var clip = await GetClipAsync(id, context, ct);
        if (clip.OwnerId != Owner(http) && !UserPermissionNames.IsGranted(http.User, UserPermissionNames.MANAGE_CLIPS))
        {
            throw new InfrastructureException(MessageCode.LIBRARY_ACCESS_DENIED, 403);
        }
        return clip;
    }

    private static string ResolveAvailableFile(Clip clip, IAppPaths paths)
    {
        if (clip.IsTrashed || clip.PublicationPending || clip.ExpiresAtUtc <= DateTime.UtcNow)
        {
            throw new InfrastructureException(MessageCode.LIBRARY_CLIP_EXPIRED, 410);
        }
        var path = MediaPaths.Resolve(paths.RecordsDirectory, clip.RelativePath);
        return File.Exists(path) ? path : throw new InfrastructureException(MessageCode.LIBRARY_FILE_NOT_FOUND, 410);
    }
}
