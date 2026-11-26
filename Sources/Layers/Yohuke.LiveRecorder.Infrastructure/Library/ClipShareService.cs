using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Library;

/// <summary>管理切片分享的可见性、访问令牌和提取密码。</summary>
public sealed class ClipShareService(IMongoContext context, IAppPaths paths, TimeProvider clock, IPasswordHasher<Clip> passwordHasher)
{
    public const string PUBLIC_VISIBILITY = "public";
    public const string PRIVATE_VISIBILITY = "private";
    private IMongoCollection<Clip> Clips => context.Collection<Clip>("clips");

    /// <summary>按创建时间和标识倒序读取仍可访问的公开分享。</summary>
    public async Task<IReadOnlyList<Clip>> ListPublicAsync(string ownerId, DateTime? beforeUtc, string? beforeId,
        int count, CancellationToken cancellationToken)
    {
        var filter = Builders<Clip>.Filter;
        var now = clock.GetUtcNow().UtcDateTime;
        var match = filter.Eq(clip => clip.OwnerId, ownerId)
                    & filter.Type(clip => clip.ShareToken, BsonType.String) & filter.Ne(clip => clip.ShareToken, "")
                    & (filter.Eq(clip => clip.ShareVisibility, PUBLIC_VISIBILITY) | filter.Eq(clip => clip.ShareVisibility, null))
                    & filter.Ne(clip => clip.IsTrashed, true) & filter.Ne(clip => clip.PublicationPending, true)
                    & filter.Gt(clip => clip.ExpiresAtUtc, now);
        if (beforeUtc is { } before && beforeId is not null)
        {
            match &= filter.Lt(clip => clip.CreatedAtUtc, before)
                     | (filter.Eq(clip => clip.CreatedAtUtc, before) & filter.Lt(clip => clip.Id, beforeId));
        }
        var items = new List<Clip>(count);
        using var cursor = await Clips.Find(match).SortByDescending(clip => clip.CreatedAtUtc).ThenByDescending(clip => clip.Id)
            .ToCursorAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var clip in cursor.Current)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    ResolveAvailableFile(clip);
                }
                catch (InfrastructureException)
                {
                    continue;
                }
                items.Add(clip);
                if (items.Count == count)
                {
                    return items;
                }
            }
        }
        return items;
    }

    /// <summary>读取切片所有者或管理员可管理的当前分享状态。</summary>
    public async Task<(string? Token, string? Visibility)> GetSettingsAsync(string id, string userId, bool canManageAll, CancellationToken cancellationToken)
    {
        var clip = await GetManagedClipAsync(id, userId, canManageAll, cancellationToken);
        return (clip.ShareToken, GetVisibility(clip));
    }

    /// <summary>兼容现有链接创建接口，首次公开分享，之后返回现有分享。</summary>
    public async Task<(string? Token, string? Visibility)> CreateAsync(string id, string userId, bool canManageAll, CancellationToken cancellationToken)
    {
        var clip = await GetManagedClipAsync(id, userId, canManageAll, cancellationToken);
        ResolveAvailableFile(clip);

        var tokenField = nameof(Clip.ShareToken);
        var visibilityField = nameof(Clip.ShareVisibility);
        var passwordField = nameof(Clip.SharePasswordHash);
        var currentToken = new BsonDocument("$ifNull", new BsonArray { "$" + tokenField, BsonNull.Value });
        var newShare = new BsonDocument("$eq", new BsonArray { currentToken, BsonNull.Value });
        var update = new PipelineUpdateDefinition<Clip>(new[]
        {
            new BsonDocument("$set", new BsonDocument
            {
                { tokenField, new BsonDocument("$ifNull", new BsonArray { "$" + tokenField, NewToken() }) },
                { visibilityField, new BsonDocument("$cond", new BsonArray { newShare, PUBLIC_VISIBILITY, "$" + visibilityField }) },
                { passwordField, new BsonDocument("$cond", new BsonArray { newShare, "$$REMOVE", "$" + passwordField }) }
            })
        });
        var shared = await Clips.FindOneAndUpdateAsync(AvailableClip(clip), update,
            new FindOneAndUpdateOptions<Clip> { ReturnDocument = ReturnDocument.After }, cancellationToken)
            ?? throw new InfrastructureException(MessageCode.LIBRARY_CLIP_EXPIRED, 410);
        return (shared.ShareToken, GetVisibility(shared));
    }

    /// <summary>比较当前令牌后保存设置，可见性或密码变化时替换令牌。</summary>
    public async Task<(string? Token, string? Visibility)> SetSettingsAsync(string id, string userId, bool canManageAll,
        string? expectedToken, string visibility, string? password, CancellationToken cancellationToken)
    {
        var clip = await GetManagedClipAsync(id, userId, canManageAll, cancellationToken);
        if (clip.ShareToken != expectedToken)
        {
            throw SettingsConflict();
        }
        ResolveAvailableFile(clip);
        if (visibility is not (PUBLIC_VISIBILITY or PRIVATE_VISIBILITY))
        {
            throw InvalidSettings();
        }

        var currentVisibility = GetVisibility(clip);
        var currentHash = currentVisibility == PRIVATE_VISIBILITY ? clip.SharePasswordHash : null;
        string? nextHash = null;
        if (visibility == PRIVATE_VISIBILITY)
        {
            if (string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(currentHash))
            {
                nextHash = currentHash;
            }
            else
            {
                if (!IsValidPassword(password))
                {
                    throw InvalidSettings();
                }
                nextHash = currentHash is { Length: > 0 } && PasswordMatches(clip, password!)
                    ? currentHash
                    : passwordHasher.HashPassword(clip, password!);
            }
        }
        if (visibility == currentVisibility && nextHash == currentHash)
        {
            return (clip.ShareToken, currentVisibility);
        }

        var token = NewToken();
        var filter = AvailableClip(clip) & Builders<Clip>.Filter.Eq(value => value.ShareToken, expectedToken);
        var update = Builders<Clip>.Update.Set(value => value.ShareToken, token).Set(value => value.ShareVisibility, visibility);
        update = nextHash is null
            ? update.Unset(value => value.SharePasswordHash)
            : update.Set(value => value.SharePasswordHash, nextHash);
        var result = await Clips.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
        if (result.MatchedCount == 0)
        {
            throw SettingsConflict();
        }
        return (token, visibility);
    }

    /// <summary>撤销指定令牌，保留其他请求已创建的新令牌。</summary>
    public async Task RevokeAsync(string id, string token, string userId, bool canManageAll, CancellationToken cancellationToken)
    {
        var clip = await GetManagedClipAsync(id, userId, canManageAll, cancellationToken);
        await Clips.UpdateOneAsync(value => value.Id == id && value.OwnerId == clip.OwnerId && value.ShareToken == token,
            ClearShare(), cancellationToken: cancellationToken);
    }

    /// <summary>按有效令牌读取切片，私密分享还需要已验证的提取凭证。</summary>
    public async Task<(Clip Clip, string Path)> GetSharedAsync(string id, string token, bool unlocked, CancellationToken cancellationToken)
    {
        var clip = await FindSharedAsync(id, token, cancellationToken);
        var path = ResolveAvailableFile(clip);
        if (GetVisibility(clip) == PRIVATE_VISIBILITY && !unlocked)
        {
            throw new InfrastructureException(MessageCode.LIBRARY_CLIP_SHARE_PASSWORD_REQUIRED, 403);
        }
        return (clip, path);
    }

    /// <summary>验证当前私密分享的提取密码，公开分享无需签发凭证。</summary>
    public async Task<bool> UnlockAsync(string id, string token, string? password, CancellationToken cancellationToken)
    {
        var clip = await FindSharedAsync(id, token, cancellationToken);
        ResolveAvailableFile(clip);
        if (GetVisibility(clip) != PRIVATE_VISIBILITY)
        {
            return false;
        }
        if (!IsValidPassword(password) || !PasswordMatches(clip, password!))
        {
            throw new InfrastructureException(MessageCode.LIBRARY_CLIP_SHARE_PASSWORD_INVALID, 403);
        }
        return true;
    }

    /// <summary>读取已发布分享的有效可见性，兼容旧链接缺少的可见性字段。</summary>
    public static string? GetVisibility(Clip clip) => clip.ShareToken is null ? null
        : clip.ShareVisibility is null or PUBLIC_VISIBILITY ? PUBLIC_VISIBILITY : PRIVATE_VISIBILITY;

    internal static UpdateDefinition<Clip> ClearShare() => Builders<Clip>.Update
        .Unset(clip => clip.ShareToken).Unset(clip => clip.ShareVisibility).Unset(clip => clip.SharePasswordHash);

    private async Task<Clip> FindSharedAsync(string id, string token, CancellationToken cancellationToken) =>
        await Clips.Find(value => value.Id == id && value.ShareToken == token).FirstOrDefaultAsync(cancellationToken)
        ?? throw new InfrastructureException(MessageCode.LIBRARY_CLIP_NOT_FOUND, 404);

    private FilterDefinition<Clip> AvailableClip(Clip clip)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return Builders<Clip>.Filter.Where(value => value.Id == clip.Id && value.OwnerId == clip.OwnerId
            && value.RelativePath == clip.RelativePath && !value.IsTrashed && value.ExpiresAtUtc > now)
            & Builders<Clip>.Filter.Ne(value => value.PublicationPending, true);
    }

    private bool PasswordMatches(Clip clip, string password) =>
        !string.IsNullOrEmpty(clip.SharePasswordHash)
        && passwordHasher.VerifyHashedPassword(clip, clip.SharePasswordHash, password) != PasswordVerificationResult.Failed;

    private static bool IsValidPassword(string? password) => password is { Length: >= 4 and <= 64 }
        && !string.IsNullOrWhiteSpace(password) && !password.Any(char.IsControl);

    private static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static InfrastructureException InvalidSettings() => new(MessageCode.LIBRARY_CLIP_SHARE_SETTINGS_INVALID);

    private static InfrastructureException SettingsConflict() => new(MessageCode.LIBRARY_CLIP_SHARE_SETTINGS_CONFLICT, 409);

    private async Task<Clip> GetManagedClipAsync(string id, string userId, bool canManageAll, CancellationToken cancellationToken)
    {
        var clip = await Clips.Find(value => value.Id == id).FirstOrDefaultAsync(cancellationToken)
                   ?? throw new InfrastructureException(MessageCode.LIBRARY_CLIP_NOT_FOUND, 404);
        if (!canManageAll && clip.OwnerId != userId)
        {
            throw new InfrastructureException(MessageCode.LIBRARY_ACCESS_DENIED, 403);
        }
        return clip;
    }

    private string ResolveAvailableFile(Clip clip)
    {
        if (clip.IsTrashed || clip.PublicationPending || clip.ExpiresAtUtc <= clock.GetUtcNow().UtcDateTime)
        {
            throw new InfrastructureException(MessageCode.LIBRARY_CLIP_EXPIRED, 410);
        }
        var file = MediaPaths.Resolve(paths.RecordsDirectory, clip.RelativePath);
        if (!File.Exists(file))
        {
            throw new InfrastructureException(MessageCode.LIBRARY_FILE_NOT_FOUND, 410);
        }
        return file;
    }
}
