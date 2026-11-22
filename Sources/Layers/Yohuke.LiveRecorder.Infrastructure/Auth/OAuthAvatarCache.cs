using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

/// <summary>按账号保存最近一次成功获取的 OAuth 头像。</summary>
public sealed class OAuthAvatarCache(IMongoContext context, IHttpClientFactory clients, ILogger<OAuthAvatarCache> logger)
{
    internal const string HTTP_CLIENT = "oauth-avatar";
    internal const string ROUTE_PREFIX = "/avatars/";
    private const int MAX_AVATAR_BYTES = 2 * 1024 * 1024;
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(5);
    private IMongoCollection<OAuthAvatar> Avatars => context.Collection<OAuthAvatar>("oauth_avatars");

    internal async Task<OAuthAvatar?> GetAsync(string id, CancellationToken cancellationToken) =>
        await Avatars.Find(avatar => avatar.Id == id).FirstOrDefaultAsync(cancellationToken);

    internal async Task<UserProfile> RefreshAsync(UserProfile user, string source, CancellationToken cancellationToken)
    {
        if (OAuthAvatarHttp.GetSource(source) is not { } uri)
        {
            return user;
        }
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        refresh.CancelAfter(RefreshTimeout);
        var token = refresh.Token;
        try
        {
            using var client = clients.CreateClient(HTTP_CLIENT);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MAX_AVATAR_BYTES)
            {
                return user;
            }
            await response.Content.LoadIntoBufferAsync(MAX_AVATAR_BYTES, token);
            var content = await response.Content.ReadAsByteArrayAsync(token);
            if (GetContentType(content) is not { } contentType)
            {
                return user;
            }
            var etag = $"\"{Convert.ToHexString(SHA256.HashData(content))}\"";
            var avatar = new OAuthAvatar(user.Id, contentType, content, etag);
            await Avatars.ReplaceOneAsync(item => item.Id == user.Id, avatar, new ReplaceOptions { IsUpsert = true }, token);
            var avatarUrl = $"/api/auth{ROUTE_PREFIX}{Uri.EscapeDataString(user.Id)}";
            if (user.AvatarUrl == avatarUrl)
            {
                return user;
            }
            var users = context.Collection<UserAccount>("users");
            var update = await users.UpdateOneAsync(item => item.Id == user.Id, Builders<UserAccount>.Update.Set(item => item.AvatarUrl, avatarUrl), cancellationToken: token);
            return update.MatchedCount == 1 ? user with { AvatarUrl = avatarUrl } : user;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("OAuth avatar refresh timed out.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or MongoException or TimeoutException)
        {
            logger.LogDebug("OAuth avatar refresh failed ({ErrorType}).", exception.GetType().Name);
        }
        return user;
    }

    private static string? GetContentType(ReadOnlySpan<byte> content)
    {
        if (content.Length < 12)
        {
            return null;
        }
        if (content.StartsWith<byte>([0xff, 0xd8, 0xff]))
        {
            return "image/jpeg";
        }
        if (content.StartsWith<byte>([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))
        {
            return "image/png";
        }
        if (content.StartsWith("GIF87a"u8) || content.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }
        return content.StartsWith("RIFF"u8) && content[8..].StartsWith("WEBP"u8) ? "image/webp" : null;
    }
}
