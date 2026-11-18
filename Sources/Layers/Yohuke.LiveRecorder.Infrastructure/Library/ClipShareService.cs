using System.Buffers.Text;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Library;

/// <summary>管理独立于切片标识的分享令牌，并验证分享文件的可用性。</summary>
public sealed class ClipShareService(IMongoContext context, IAppPaths paths, TimeProvider clock)
{
    private IMongoCollection<Clip> Clips => context.Collection<Clip>("clips");

    /// <summary>读取所有者或管理员管理的当前分享令牌。</summary>
    public async Task<string?> GetTokenAsync(string id, string userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var clip = await GetManagedClipAsync(id, userId, isAdmin, cancellationToken);
        return clip.ShareToken;
    }

    /// <summary>为可用切片创建分享令牌，或返回已存在的令牌。</summary>
    public async Task<string> CreateAsync(string id, string userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var clip = await GetManagedClipAsync(id, userId, isAdmin, cancellationToken);
        ResolveAvailableFile(clip);

        var now = clock.GetUtcNow().UtcDateTime;
        var filter = Builders<Clip>.Filter.Where(value => value.Id == id && value.OwnerId == clip.OwnerId
            && value.RelativePath == clip.RelativePath && !value.IsTrashed && value.ExpiresAtUtc > now)
            & Builders<Clip>.Filter.Ne(value => value.PublicationPending, true);
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var field = nameof(Clip.ShareToken);
        var update = new PipelineUpdateDefinition<Clip>(new[]
        {
            new BsonDocument("$set", new BsonDocument(field,
                new BsonDocument("$ifNull", new BsonArray { $"${field}", token })))
        });
        var shared = await Clips.FindOneAndUpdateAsync(filter, update,
            new FindOneAndUpdateOptions<Clip> { ReturnDocument = ReturnDocument.After }, cancellationToken)
            ?? throw new InfrastructureException(MessageCode.LIBRARY_CLIP_EXPIRED, 410);
        return shared.ShareToken!;
    }

    /// <summary>撤销指定令牌，保留其他请求已创建的新令牌。</summary>
    public async Task RevokeAsync(string id, string token, string userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var clip = await GetManagedClipAsync(id, userId, isAdmin, cancellationToken);
        await Clips.UpdateOneAsync(value => value.Id == id && value.OwnerId == clip.OwnerId && value.ShareToken == token,
            Builders<Clip>.Update.Unset(value => value.ShareToken), cancellationToken: cancellationToken);
    }

    /// <summary>按切片标识和有效令牌读取可交付的切片文件。</summary>
    public async Task<(Clip Clip, string Path)> GetSharedAsync(string id, string token, CancellationToken cancellationToken)
    {
        var clip = await Clips.Find(value => value.Id == id && value.ShareToken == token).FirstOrDefaultAsync(cancellationToken)
                   ?? throw new InfrastructureException(MessageCode.LIBRARY_CLIP_NOT_FOUND, 404);
        return (clip, ResolveAvailableFile(clip));
    }

    private async Task<Clip> GetManagedClipAsync(string id, string userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var clip = await Clips.Find(value => value.Id == id).FirstOrDefaultAsync(cancellationToken)
                   ?? throw new InfrastructureException(MessageCode.LIBRARY_CLIP_NOT_FOUND, 404);
        if (!isAdmin && clip.OwnerId != userId)
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
