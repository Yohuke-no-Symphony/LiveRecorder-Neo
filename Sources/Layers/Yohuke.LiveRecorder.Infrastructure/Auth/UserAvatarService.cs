using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

internal sealed class UserAvatarService(IMongoContext context, IAppPaths paths, TimeProvider timeProvider, ILogger<UserAvatarService> logger)
{
    private IMongoCollection<UserAccount> Users => context.Collection<UserAccount>("users");

    internal async Task<UserAvatarImage?> ReadAsync(string id, string? requestedRevision, CancellationToken ct)
    {
        var user = await Users.Find(value => value.Id == id)
            .Project(value => new { value.AvatarUrl, value.AvatarRevision }).FirstOrDefaultAsync(ct);
        if (user?.AvatarRevision is not { } revision)
        {
            return null;
        }
        var requestedUrl = requestedRevision is null ? UserAvatarFiles.LegacyUrl(id) : UserAvatarFiles.VersionedUrl(id, requestedRevision);
        if (user.AvatarUrl != requestedUrl || requestedRevision is not null && requestedRevision != revision)
        {
            return null;
        }
        return await UserAvatarFiles.ReadAsync(paths, id, revision, ct);
    }

    internal Task<UserProfile> SetAsync(string id, long sessionVersion, byte[] content, CancellationToken ct) =>
        SaveAsync(id, sessionVersion, content, ct);

    internal Task<UserProfile> ClearAsync(string id, long sessionVersion, CancellationToken ct) =>
        SaveAsync(id, sessionVersion, null, ct);

    private async Task<UserProfile> SaveAsync(string id, long sessionVersion, byte[]? content, CancellationToken ct)
    {
        var revision = Guid.NewGuid().ToString("N");
        var url = content is null ? "" : UserAvatarFiles.VersionedUrl(id, revision);
        if (content is not null)
        {
            await UserAvatarFiles.WriteAsync(paths, id, revision, content, ct);
        }
        var filter = Builders<UserAccount>.Filter.Where(user => user.Id == id && user.Status == "Active")
                     & AccountSessionVersion.Filter(sessionVersion);
        var update = Builders<UserAccount>.Update.Set(user => user.AvatarRevision, revision).Set(user => user.AvatarUrl, url)
            .Set(user => user.UpdatedAtUtc, timeProvider.GetUtcNow().UtcDateTime);
        var previous = await Users.FindOneAndUpdateAsync(filter, update, cancellationToken: ct);
        if (previous is null)
        {
            UserAvatarFiles.TryDelete(paths, id, revision, logger);
            throw new InfrastructureException(MessageCode.REQUEST_UNAUTHORIZED, 401);
        }
        UserAvatarFiles.TryDelete(paths, id, previous.AvatarRevision, logger);
        return IdentityService.Profile(previous) with { AvatarUrl = url };
    }
}
