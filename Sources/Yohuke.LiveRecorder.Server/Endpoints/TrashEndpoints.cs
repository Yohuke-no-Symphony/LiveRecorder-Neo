using System.Security.Claims;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Storage;
using Yohuke.LiveRecorder.Server.Contracts;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>隔离个人切片回收站和按资源类型授权的回收站管理。</summary>
public static class TrashEndpoints
{
    private const int MAX_PURGE_ENTRIES = 200;

    /// <summary>注册两个回收站的查询、恢复与彻底删除接口。</summary>
    public static IEndpointRouteBuilder MapTrashEndpoints(this IEndpointRouteBuilder endpoints)
    {
        Map(endpoints.MapGroup("/api/trash").RequireAuthorization(), management: false);
        var management = endpoints.MapGroup("/api/admin/trash").RequireAuthorization(policy =>
            policy.RequireAuthenticatedUser().RequireAssertion(context =>
                CanManageClips(context.User) || CanListRecordings(context.User)));
        Map(management, management: true);
        return endpoints;
    }

    private static void Map(RouteGroupBuilder group, bool management)
    {
        group.MapGet("", async (HttpContext http, IMongoContext context, ISettingsStore settingsStore, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "private, no-store";
            var filter = management ? ManagedFilter(http.User)
                : Builders<TrashEntry>.Filter.Where(entry => entry.Kind == "Clip" && entry.OwnerId == Owner(http.User));
            var entries = await context.Collection<TrashEntry>("trash").Find(filter)
                .SortByDescending(entry => entry.CreatedAtUtc).ThenByDescending(entry => entry.Id).ToListAsync(ct);
            var settings = await settingsStore.GetAsync(ct);
            foreach (var entry in entries)
            {
                entry.PurgeAtUtc = TrashRetentionPolicy.GetPurgeAtUtc(entry, settings);
            }
            return Results.Ok(entries);
        });
        group.MapPost("/{id}/restore", async (string id, HttpContext http, IMongoContext context, StorageService storage,
            SessionTrashService sessionTrash, SessionSegmentTrashService segmentTrash, CancellationToken ct) =>
        {
            var entry = await GetEntryAsync(id, context, ct);
            RequireAccess(entry, http.User, management, UserPermissionNames.MANAGE_RECORDINGS);
            await (entry.Kind switch
            {
                "Segment" => segmentTrash.RestoreAsync(entry, ct),
                "Session" => sessionTrash.RestoreAsync(entry, ct),
                _ => storage.RestoreAsync(entry, ct)
            });
            return Results.NoContent();
        });
        group.MapDelete("/{id}", async (string id, HttpContext http, IMongoContext context, StorageService storage,
            SessionTrashService sessionTrash, SessionSegmentTrashService segmentTrash, CancellationToken ct) =>
        {
            var entry = await GetEntryAsync(id, context, ct);
            RequireAccess(entry, http.User, management, UserPermissionNames.PURGE_RECORDINGS);
            await PurgeAsync(entry, storage, sessionTrash, segmentTrash, ct);
            return Results.NoContent();
        });
        group.MapPost("/purge", (TrashPurgeRequest request, HttpContext http, IMongoContext context, StorageService storage,
            SessionTrashService sessionTrash, SessionSegmentTrashService segmentTrash, CancellationToken ct) =>
            PurgeBatchAsync(request, http.User, management, context, storage, sessionTrash, segmentTrash, ct));
    }

    private static FilterDefinition<TrashEntry> ManagedFilter(ClaimsPrincipal user)
    {
        var kinds = new List<string>(3);
        if (CanManageClips(user))
        {
            kinds.Add("Clip");
        }
        if (CanListRecordings(user))
        {
            kinds.AddRange(["Session", "Segment"]);
        }
        return Builders<TrashEntry>.Filter.In(entry => entry.Kind, kinds);
    }

    private static bool CanManageClips(ClaimsPrincipal user) =>
        UserPermissionNames.IsGranted(user, UserPermissionNames.MANAGE_CLIPS);

    private static bool CanListRecordings(ClaimsPrincipal user) =>
        UserPermissionNames.IsGranted(user, UserPermissionNames.MANAGE_RECORDINGS) ||
        UserPermissionNames.IsGranted(user, UserPermissionNames.PURGE_RECORDINGS);

    private static string Owner(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static async Task<TrashEntry> GetEntryAsync(string id, IMongoContext context, CancellationToken ct) =>
        await context.Collection<TrashEntry>("trash").Find(entry => entry.Id == id).FirstOrDefaultAsync(ct)
        ?? throw new InfrastructureException(MessageCode.LIBRARY_TRASH_NOT_FOUND, 404);

    private static void RequireAccess(TrashEntry entry, ClaimsPrincipal user, bool management, string recordingPermission)
    {
        var allowed = management ? entry.Kind switch
        {
            "Clip" => CanManageClips(user),
            "Session" or "Segment" => UserPermissionNames.IsGranted(user, recordingPermission),
            _ => false
        } : entry.Kind == "Clip" && entry.OwnerId == Owner(user);
        if (!allowed)
        {
            throw new InfrastructureException(MessageCode.LIBRARY_ACCESS_DENIED, 403);
        }
    }

    private static async Task<IResult> PurgeBatchAsync(TrashPurgeRequest request, ClaimsPrincipal user, bool management,
        IMongoContext context, StorageService storage, SessionTrashService sessionTrash,
        SessionSegmentTrashService segmentTrash, CancellationToken ct)
    {
        if (request.EntryIds is null || request.EntryIds.Count is < 1 or > MAX_PURGE_ENTRIES ||
            request.EntryIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new InfrastructureException(MessageCode.LIBRARY_PURGE_COUNT_INVALID);
        }
        var ids = request.EntryIds.Distinct(StringComparer.Ordinal).ToArray();
        var entries = await context.Collection<TrashEntry>("trash")
            .Find(Builders<TrashEntry>.Filter.In(entry => entry.Id, ids)).ToListAsync(ct);
        var entriesById = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var results = new List<TrashPurgeResult>(ids.Length);
        foreach (var id in ids.OrderBy(id => entriesById.GetValueOrDefault(id)?.Kind switch
                 {
                     "Segment" => 0,
                     "Session" => 2,
                     _ => 1
                 }))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (entriesById.TryGetValue(id, out var entry))
                {
                    RequireAccess(entry, user, management, UserPermissionNames.PURGE_RECORDINGS);
                    await PurgeAsync(entry, storage, sessionTrash, segmentTrash, ct);
                }
                results.Add(new() { EntryId = id, Success = true });
            }
            catch (InfrastructureException error)
            {
                results.Add(new() { EntryId = id, Error = LocalizedText.Format(error.Code, error.Message, error.Arguments) });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                results.Add(new() { EntryId = id, Error = LocalizedText.Get(MessageCode.LIBRARY_PURGE_FILES_FAILED) });
            }
        }
        return Results.Ok(results);
    }

    private static Task PurgeAsync(TrashEntry entry, StorageService storage, SessionTrashService sessionTrash,
        SessionSegmentTrashService segmentTrash, CancellationToken ct) => entry.Kind switch
    {
        "Segment" => segmentTrash.PurgeAsync(entry, ct),
        "Session" => sessionTrash.PurgeAsync(entry, ct),
        _ => storage.PurgeAsync(entry, ct)
    };
}
