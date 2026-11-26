using MongoDB.Bson;
using MongoDB.Driver;

namespace Yohuke.LiveRecorder.Infrastructure.Persistence.Migrations;

internal sealed class MigrationStateStore(IMongoDatabase database)
{
    private const string STATE_ID = "server";
    private const int LEASE_SECONDS = 120;
    private readonly IMongoCollection<MigrationState> states = database.GetCollection<MigrationState>("schema_migrations")
        .WithReadPreference(ReadPreference.Primary);
    private static readonly FilterDefinitionBuilder<MigrationState> Filters = Builders<MigrationState>.Filter;
    private static readonly UpdateDefinitionBuilder<MigrationState> Updates = Builders<MigrationState>.Update;
    private static readonly FilterDefinition<MigrationState> ById = Filters.Eq(value => value.Id, STATE_ID);

    internal Task<MigrationState> ReadAsync(CancellationToken cancellationToken) =>
        states.Find(ById).FirstOrDefaultAsync(cancellationToken);

    internal async Task InitializeAsync(string version, CancellationToken cancellationToken)
    {
        try
        {
            await states.UpdateOneAsync(ById, Updates.SetOnInsert(value => value.Version, version),
                new() { IsUpsert = true }, cancellationToken);
        }
        catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
        }
    }

    internal Task<MigrationState> AcquireAsync(string version, string leaseId, CancellationToken cancellationToken) =>
        states.FindOneAndUpdateAsync(ById & Filters.Eq(value => value.Version, version) &
            (Filters.Eq(value => value.LeaseId, null) | CompareLease("$lte")), ExtendLease(leaseId),
            new() { ReturnDocument = ReturnDocument.After }, cancellationToken);

    internal async Task RenewAsync(string leaseId, CancellationToken cancellationToken)
    {
        RequireMatch(await states.UpdateOneAsync(Owned(leaseId), ExtendLease(leaseId), cancellationToken: cancellationToken));
    }

    internal async Task BeginAsync(MigrationStep step, string leaseId, CancellationToken cancellationToken)
    {
        RequireMatch(await states.UpdateOneAsync(Owned(leaseId) & Filters.Eq(value => value.Version, step.From) &
            Filters.Eq(value => value.PendingTarget, null), Updates.Set(value => value.PendingTarget, step.To),
            cancellationToken: cancellationToken));
    }

    internal async Task SaveCheckpointAsync(MigrationStep step, string leaseId, string checkpoint, CancellationToken cancellationToken)
    {
        RequireMatch(await states.UpdateOneAsync(ActiveStep(step, leaseId), Updates.Set(value => value.Checkpoint, checkpoint),
            cancellationToken: cancellationToken));
    }

    internal async Task CommitAsync(MigrationStep step, string leaseId, CancellationToken cancellationToken)
    {
        RequireMatch(await states.UpdateOneAsync(ActiveStep(step, leaseId),
            Updates.Set(value => value.Version, step.To).Unset(value => value.PendingTarget).Unset(value => value.Checkpoint),
            cancellationToken: cancellationToken));
    }

    internal Task ReleaseAsync(string leaseId, CancellationToken cancellationToken) =>
        states.UpdateOneAsync(ById & Filters.Eq(value => value.LeaseId, leaseId),
            Updates.Unset(value => value.LeaseId).Unset(value => value.LeaseUntilUtc), cancellationToken: cancellationToken);

    private static FilterDefinition<MigrationState> Owned(string leaseId) =>
        ById & Filters.Eq(value => value.LeaseId, leaseId) & CompareLease("$gt");

    private static FilterDefinition<MigrationState> ActiveStep(MigrationStep step, string leaseId) =>
        Owned(leaseId) & Filters.Eq(value => value.Version, step.From) & Filters.Eq(value => value.PendingTarget, step.To);

    private static FilterDefinition<MigrationState> CompareLease(string comparison) =>
        new BsonDocument("$expr", new BsonDocument(comparison, new BsonArray
        {
            "$" + nameof(MigrationState.LeaseUntilUtc), "$$NOW"
        }));

    private static UpdateDefinition<MigrationState> ExtendLease(string leaseId) =>
        new PipelineUpdateDefinition<MigrationState>(new[]
        {
            new BsonDocument("$set", new BsonDocument
            {
                { nameof(MigrationState.LeaseId), leaseId },
                { nameof(MigrationState.LeaseUntilUtc), new BsonDocument("$dateAdd", new BsonDocument
                    { { "startDate", "$$NOW" }, { "unit", "second" }, { "amount", LEASE_SECONDS } }) }
            })
        });

    private static void RequireMatch(UpdateResult result)
    {
        if (result.MatchedCount != 1)
        {
            throw new InvalidOperationException("The active data migration lease or version changed.");
        }
    }
}
