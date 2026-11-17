using System.Text.Json;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Library;

/// <summary>按所属用户隔离并维护场次真实时间轴上的个人标记。</summary>
public sealed class SessionBookmarkService(IMongoContext context, LibraryService library, IAppPaths paths)
{
    /// <summary>按时间和稳定标识读取当前用户在指定场次的标记。</summary>
    public async Task<IReadOnlyList<SessionBookmark>> ListAsync(string sessionId, string ownerId, CancellationToken cancellationToken = default)
    {
        await library.GetAsync(sessionId, cancellationToken);
        return await context.Collection<SessionBookmark>("sessionBookmarks")
            .Find(x => x.SessionId == sessionId && x.OwnerId == ownerId)
            .Sort(Builders<SessionBookmark>.Sort.Ascending(x => x.OffsetMs).Ascending(x => x.Id))
            .ToListAsync(cancellationToken);
    }

    /// <summary>在当前用户的场次中保存一个命名时间点。</summary>
    public async Task<SessionBookmark> CreateAsync(string sessionId, string ownerId, string? title, double? offsetMs, CancellationToken cancellationToken = default)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var detail = await RequireEditableAsync(sessionId, cancellationToken);
        var bookmark = new SessionBookmark
        {
            SessionId = sessionId,
            OwnerId = ownerId,
            Title = ValidateTitle(title),
            OffsetMs = ValidateOffset(offsetMs, detail.DurationMs)
        };
        await context.Collection<SessionBookmark>("sessionBookmarks").InsertOneAsync(bookmark, cancellationToken: cancellationToken);
        return bookmark;
    }

    /// <summary>更新当前用户所属标记的名称和真实时间，不更改其所属场次。</summary>
    public async Task<SessionBookmark> UpdateAsync(string sessionId, string bookmarkId, string ownerId, string? title, double? offsetMs, CancellationToken cancellationToken = default)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var bookmarks = context.Collection<SessionBookmark>("sessionBookmarks");
        var filter = OwnedBookmark(sessionId, bookmarkId, ownerId);
        if (!await bookmarks.Find(filter).AnyAsync(cancellationToken))
        {
            throw new InfrastructureException(MessageCode.BOOKMARK_NOT_FOUND, 404);
        }
        var detail = await RequireEditableAsync(sessionId, cancellationToken);
        var update = Builders<SessionBookmark>.Update
            .Set(x => x.Title, ValidateTitle(title))
            .Set(x => x.OffsetMs, ValidateOffset(offsetMs, detail.DurationMs));
        return await bookmarks.FindOneAndUpdateAsync(filter, update,
                   new FindOneAndUpdateOptions<SessionBookmark> { ReturnDocument = ReturnDocument.After }, cancellationToken)
               ?? throw new InfrastructureException(MessageCode.BOOKMARK_NOT_FOUND, 404);
    }

    /// <summary>删除当前用户在指定场次中拥有的标记。</summary>
    public async Task DeleteAsync(string sessionId, string bookmarkId, string ownerId, CancellationToken cancellationToken = default)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var bookmarks = context.Collection<SessionBookmark>("sessionBookmarks");
        var filter = OwnedBookmark(sessionId, bookmarkId, ownerId);
        if (!await bookmarks.Find(filter).AnyAsync(cancellationToken))
        {
            throw new InfrastructureException(MessageCode.BOOKMARK_NOT_FOUND, 404);
        }
        await RequireEditableAsync(sessionId, cancellationToken);
        var result = await bookmarks.DeleteOneAsync(filter, cancellationToken);
        if (result.DeletedCount == 0)
        {
            throw new InfrastructureException(MessageCode.BOOKMARK_NOT_FOUND, 404);
        }
    }

    private async Task<SessionDetail> RequireEditableAsync(string sessionId, CancellationToken cancellationToken)
    {
        var detail = await library.GetAsync(sessionId, cancellationToken);
        if (detail.Session.IsHidden)
        {
            throw new InfrastructureException(MessageCode.BOOKMARK_SESSION_NOT_EDITABLE, 409);
        }
        var operations = Path.Combine(paths.RecordsDirectory, ".work", "groups");
        if (Directory.Exists(operations))
        {
            foreach (var file in Directory.EnumerateFiles(operations, "operation.json", SearchOption.AllDirectories))
            {
                await using var input = File.OpenRead(file);
                var operation = await JsonSerializer.DeserializeAsync<ProjectionOperation>(input, JsonFiles.Options, cancellationToken);
                if (operation is not null && operation.SourceIds.Contains(sessionId))
                {
                    throw new InfrastructureException(MessageCode.BOOKMARK_SESSION_NOT_EDITABLE, 409);
                }
            }
        }
        return detail;
    }

    private static string ValidateTitle(string? title)
    {
        var value = title?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 180)
        {
            throw new InfrastructureException(MessageCode.BOOKMARK_TITLE_INVALID);
        }
        return value;
    }

    private static double ValidateOffset(double? offsetMs, double durationMs)
    {
        if (offsetMs is null || !double.IsFinite(offsetMs.Value) || offsetMs.Value < 0 || offsetMs.Value > durationMs)
        {
            throw new InfrastructureException(MessageCode.BOOKMARK_TIME_INVALID);
        }
        return offsetMs.Value;
    }

    private static FilterDefinition<SessionBookmark> OwnedBookmark(string sessionId, string bookmarkId, string ownerId)
    {
        return Builders<SessionBookmark>.Filter.Where(x => x.Id == bookmarkId && x.SessionId == sessionId && x.OwnerId == ownerId);
    }
}
