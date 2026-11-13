using System.Text.Json.Nodes;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证回收站周期变更作用于既有录像与切片，零天关闭自动彻底删除。</summary>
public sealed class TrashRetentionTests
{
    /// <summary>关闭、延长和缩短周期都按原回收时间影响既有条目。</summary>
    [Theory]
    [InlineData("Session")]
    [InlineData("Clip")]
    public async Task CurrentPolicyControlsExistingTrashEntries(string kind)
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        var entry = await SeedTrashAsync(fixture, service, kind);
        var createdAt = DateTime.UtcNow.AddDays(-10);
        await fixture.Mongo.Collection<TrashEntry>("trash").UpdateOneAsync(x => x.Id == entry.Id,
            Builders<TrashEntry>.Update.Set(x => x.CreatedAtUtc, createdAt));
        if (kind == "Session")
        {
            var path = Path.Combine(fixture.Root, ".trash", entry.Id, "session-entry.json");
            var journal = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            journal["entry"]!["createdAtUtc"] = createdAt;
            await File.WriteAllTextAsync(path, journal.ToJsonString());
        }
        fixture.Settings.Settings.TrashRetentionDays = 0;

        await MaintainAsync(fixture, service, kind);

        var current = await EntryAsync(fixture, entry.Id);
        Assert.Null(current.PurgeAtUtc);
        Assert.True(TrashExists(fixture, current));
        fixture.Settings.Settings.TrashRetentionDays = 30;
        await MaintainAsync(fixture, service, kind);
        current = await EntryAsync(fixture, entry.Id);
        Assert.True(current.PurgeAtUtc > DateTime.UtcNow.AddDays(19));
        Assert.True(TrashExists(fixture, current));
        fixture.Settings.Settings.TrashRetentionDays = 1;
        await MaintainAsync(fixture, service, kind);
        Assert.False(await fixture.Mongo.Collection<TrashEntry>("trash").Find(x => x.Id == entry.Id).AnyAsync());
        Assert.False(TrashExists(fixture, current));
    }

    /// <summary>自动彻底删除被关闭时，新条目没有期限，手动彻底删除仍然有效。</summary>
    [Theory]
    [InlineData("Session")]
    [InlineData("Clip")]
    public async Task DisabledAutomaticPurgeStillAllowsExplicitPurge(string kind)
    {
        await using var fixture = await JobTestContext.CreateAsync();
        fixture.Settings.Settings.TrashRetentionDays = 0;
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        var entry = await SeedTrashAsync(fixture, service, kind);
        Assert.Null(entry.PurgeAtUtc);
        await MaintainAsync(fixture, service, kind);
        Assert.True(TrashExists(fixture, entry));

        if (kind == "Session")
        {
            await service.PurgeAsync(entry);
        }
        else
        {
            await fixture.Storage.PurgeAsync(entry);
        }

        Assert.False(await fixture.Mongo.Collection<TrashEntry>("trash").Find(x => x.Id == entry.Id).AnyAsync());
        Assert.False(TrashExists(fixture, entry));
    }

    private static Task<TrashEntry> EntryAsync(JobTestContext fixture, string id) =>
        fixture.Mongo.Collection<TrashEntry>("trash").Find(x => x.Id == id).SingleAsync();

    private static Task MaintainAsync(JobTestContext fixture, SessionTrashService service, string kind) =>
        kind == "Session" ? service.MaintainAsync() : fixture.Storage.MaintainAsync();

    private static bool TrashExists(JobTestContext fixture, TrashEntry entry) =>
        entry.Kind == "Session"
            ? Directory.Exists(Path.Combine(fixture.Root, entry.TrashRelativePath))
            : File.Exists(Path.Combine(fixture.Root, entry.TrashRelativePath));

    private static async Task<TrashEntry> SeedTrashAsync(JobTestContext fixture, SessionTrashService service,
        string kind)
    {
        string entityId;
        if (kind == "Session")
        {
            var (session, _) = await fixture.SeedSessionAsync();
            entityId = session.Id;
            await service.TrashAsync(session.Id, "member");
        }
        else
        {
            var clip = new Clip
            {
                OwnerId = "member", RelativePath = "clips/retention.mp4", Bytes = 5,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(1)
            };
            entityId = clip.Id;
            var path = Path.Combine(fixture.Root, clip.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "media");
            await fixture.Mongo.Collection<Clip>("clips").InsertOneAsync(clip);
            await fixture.Storage.TrashClipAsync(clip);
        }

        return await fixture.Mongo.Collection<TrashEntry>("trash").Find(x => x.EntityId == entityId).SingleAsync();
    }
}
