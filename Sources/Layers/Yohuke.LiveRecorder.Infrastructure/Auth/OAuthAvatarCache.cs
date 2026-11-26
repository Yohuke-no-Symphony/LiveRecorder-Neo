using System.Net;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

/// <summary>在 OAuth 注册完成时导入外部头像。</summary>
public sealed class OAuthAvatarCache(IMongoContext context, IAppPaths paths, IHttpClientFactory clients, ILogger<OAuthAvatarCache> logger)
{
    internal const string HTTP_CLIENT = "oauth-avatar";
    private static readonly TimeSpan IMPORT_TIMEOUT = TimeSpan.FromSeconds(5);

    internal async Task<UserProfile> ImportRegistrationAvatarAsync(UserProfile user, string source, CancellationToken cancellationToken)
    {
        if (OAuthAvatarHttp.GetSource(source) is not { } uri)
        {
            return user;
        }
        using var import = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        import.CancelAfter(IMPORT_TIMEOUT);
        var token = import.Token;
        try
        {
            using var client = clients.CreateClient(HTTP_CLIENT);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > UserAvatarImage.MAX_IMAGE_BYTES)
            {
                return user;
            }
            await response.Content.LoadIntoBufferAsync(UserAvatarImage.MAX_IMAGE_BYTES, token);
            var content = await response.Content.ReadAsByteArrayAsync(token);
            if (UserAvatarImage.FromBytes(content) is null)
            {
                return user;
            }
            var revision = Guid.NewGuid().ToString("N");
            var avatarUrl = UserAvatarFiles.VersionedUrl(user.Id, revision);
            await UserAvatarFiles.WriteAsync(paths, user.Id, revision, content, token);
            var users = context.Collection<UserAccount>("users");
            var updated = await users.FindOneAndUpdateAsync(item => item.Id == user.Id && item.AvatarRevision == null && item.AvatarUrl == "",
                Builders<UserAccount>.Update.Set(item => item.AvatarRevision, revision).Set(item => item.AvatarUrl, avatarUrl),
                new FindOneAndUpdateOptions<UserAccount> { ReturnDocument = ReturnDocument.After }, token);
            if (updated is not null)
            {
                return IdentityService.Profile(updated);
            }
            UserAvatarFiles.TryDelete(paths, user.Id, revision, logger);
            var current = await users.Find(item => item.Id == user.Id).FirstOrDefaultAsync(token);
            return current is null ? user : IdentityService.Profile(current);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("OAuth avatar import timed out.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or MongoException or TimeoutException)
        {
            logger.LogDebug("OAuth avatar import failed ({ErrorType}).", exception.GetType().Name);
        }
        return user;
    }
}
