using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Auth;
using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Infrastructure.Persistence.Migrations;

[Migration("0.1", "0.2")]
internal sealed class OAuthAvatarFileMigration : IMigration
{
    private const int BATCH_SIZE = 16;
    private const string LEGACY_COLLECTION = "oauth_avatars";

    public Task PrepareAsync(MigrationContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task MigrateAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var avatars = context.Database.GetCollection<BsonDocument>(LEGACY_COLLECTION);
        var users = context.Database.GetCollection<UserAccount>("users");
        var filters = Builders<BsonDocument>.Filter;
        while (true)
        {
            var filter = filters.Type("_id", BsonType.String);
            if (context.Checkpoint is { } checkpoint)
            {
                filter &= filters.Gt("_id", checkpoint);
            }
            var batch = await avatars.Find(filter).Sort(new BsonDocument("_id", 1)).Limit(BATCH_SIZE)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                return;
            }
            foreach (var avatar in batch)
            {
                await MigrateAvatarAsync(avatar, avatars, users, context.Paths, cancellationToken);
            }
            await context.SaveCheckpointAsync(batch[^1]["_id"].AsString, cancellationToken);
        }
    }

    public async Task VerifyAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var users = context.Database.GetCollection<UserAccount>("users");
        var filter = Builders<UserAccount>.Filter.Nin(user => user.AvatarRevision, [null, ""])
            & Builders<UserAccount>.Filter.Regex(user => user.AvatarUrl, new BsonRegularExpression("^/api/auth/avatars/"));
        using var cursor = await users.Find(filter).Project(user => new { user.Id, user.AvatarUrl, user.AvatarRevision })
            .ToCursorAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var user in cursor.Current)
            {
                if (user.AvatarUrl == UserAvatarFiles.LegacyUrl(user.Id) && user.AvatarRevision is { Length: > 0 } revision)
                {
                    await VerifyFileAsync(context.Paths, user.Id, revision, cancellationToken);
                }
            }
        }
    }

    private static async Task MigrateAvatarAsync(BsonDocument avatar, IMongoCollection<BsonDocument> avatars,
        IMongoCollection<UserAccount> users, IAppPaths paths, CancellationToken cancellationToken)
    {
        var id = avatar["_id"].AsString;
        if (string.IsNullOrEmpty(id) || !avatar.TryGetValue("ETag", out var etag) || !etag.IsString
            || !avatar.TryGetValue("Content", out var binary) || !binary.IsBsonBinaryData
            || UserAvatarImage.FromBytes(binary.AsBsonBinaryData.Bytes) is not { } image)
        {
            return;
        }
        var revision = ContentRevision(image.Content);
        var legacyUrl = UserAvatarFiles.LegacyUrl(id);
        var sameUrl = Builders<UserAccount>.Filter.Where(user => user.Id == id && user.AvatarUrl == legacyUrl);
        var user = await users.Find(sameUrl).Project(candidate => new { candidate.AvatarRevision }).FirstOrDefaultAsync(cancellationToken);
        if (user is null || !string.IsNullOrEmpty(user.AvatarRevision) && user.AvatarRevision != revision)
        {
            return;
        }
        var file = await UserAvatarFiles.ReadAsync(paths, id, revision, cancellationToken);
        if (file is null || ContentRevision(file.Content) != revision)
        {
            await UserAvatarFiles.WriteAsync(paths, id, revision, image.Content, cancellationToken);
            await VerifyFileAsync(paths, id, revision, cancellationToken);
        }
        if (user.AvatarRevision != revision)
        {
            var unversioned = Builders<UserAccount>.Filter.In(candidate => candidate.AvatarRevision, [null, ""]);
            var result = await users.UpdateOneAsync(sameUrl & unversioned,
                Builders<UserAccount>.Update.Set(candidate => candidate.AvatarRevision, revision), cancellationToken: cancellationToken);
            if (result.MatchedCount == 0)
            {
                return;
            }
        }
        await avatars.DeleteOneAsync(new BsonDocument { { "_id", id }, { "ETag", etag } }, cancellationToken);
    }

    private static async Task VerifyFileAsync(IAppPaths paths, string id, string revision, CancellationToken cancellationToken)
    {
        var file = await UserAvatarFiles.ReadAsync(paths, id, revision, cancellationToken);
        if (file is null || ContentRevision(file.Content) != revision)
        {
            throw new InvalidDataException("A migrated OAuth avatar file could not be verified.");
        }
    }

    private static string ContentRevision(byte[] content) => Convert.ToHexString(SHA256.HashData(content));
}
