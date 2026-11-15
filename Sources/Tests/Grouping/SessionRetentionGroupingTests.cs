using System.Text.Json;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Grouping.Tests;

/// <summary>验证拆分和合并不会丢失永久保留与恢复后的保留起点。</summary>
public sealed class SessionRetentionGroupingTests
{
    /// <summary>合并任意永久保留来源时保留整个投影，拆分后两侧继承该选择。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GroupAndSplitPreserveRetentionMetadata(bool isPermanent)
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync(10000);
        var retainedAt = DateTime.UtcNow.AddDays(-1);
        await fixture.Mongo.Collection<BroadcastSession>("sessions").UpdateOneAsync(x => x.Id == second.Session.Id,
            Builders<BroadcastSession>.Update.Set(x => x.RetentionStartedAtUtc, retainedAt));
        second.Session.RetentionStartedAtUtc = retainedAt;
        await JsonFiles.WriteAsync(Path.Combine(fixture.Root, second.Session.DirectoryPath, "场次.json"), second);
        var retention = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await retention.SetPermanentAsync(second.Session.Id, isPermanent);

        var grouped = await fixture.Service().GroupAsync(first.Session.Id, [second.Session.Id], "retained");
        var detail = await fixture.Library.GetAsync(grouped.Id);
        var split = await fixture.Service().SplitAsync(grouped.Id,
            detail.Segments.First(segment => segment.StartOffsetMs >= 10000).Id);

        Assert.Equal(isPermanent, grouped.IsPermanent);
        Assert.Equal(retainedAt.ToString("yyyyMMddHHmmss"), grouped.RetentionStartedAtUtc!.Value.ToString("yyyyMMddHHmmss"));
        foreach (var session in split)
        {
            Assert.Equal(isPermanent, session.IsPermanent);
            Assert.Equal(grouped.RetentionStartedAtUtc, session.RetentionStartedAtUtc);
            var saved = await fixture.Mongo.Collection<BroadcastSession>("sessions").Find(x => x.Id == session.Id)
                .SingleAsync();
            Assert.Equal(isPermanent, saved.IsPermanent);
            await using var input = File.OpenRead(Path.Combine(fixture.Root, session.DirectoryPath, "场次.json"));
            var manifest = (await JsonSerializer.DeserializeAsync<SessionManifest>(input, JsonFiles.Options))!;
            Assert.Equal(isPermanent, manifest.Session.IsPermanent);
            Assert.Equal(session.RetentionStartedAtUtc, manifest.Session.RetentionStartedAtUtc);
        }
    }
}
