using System.Text;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Domain.LiveMarkers;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;
using Yohuke.LiveRecorder.Infrastructure.Recording;

namespace Yohuke.LiveRecorder.Recording.Tests;

/// <summary>验证持有媒体库锁的录制监督器可恢复附件投影而不重复获取同一锁。</summary>
public sealed class LiveMarkerReconciliationTests
{
    /// <summary>启动重建完成未发布副本后隐藏来源，锁等待不能阻塞启动。</summary>
    [RecordingFact]
    public async Task SupervisorRestoresProjectionAttachmentsWithinItsExistingLibraryLock()
    {
        await using var fixture = await RecordingFixture.CreateAsync();
        var source = new BroadcastSession
        {
            DirectoryPath = "reconciliation/source", Title = "source", State = "Completed",
            StartedAtUtc = new(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc),
            EndedAtUtc = new(2026, 11, 12, 0, 0, 2, DateTimeKind.Utc)
        };
        await JsonFiles.WriteAsync(Path.Combine(fixture.StoragePath, source.DirectoryPath, "场次.json"),
            new SessionManifest { Session = source });
        var bytes = Encoding.UTF8.GetBytes("StartTime: 2026-11-12T00:00:00Z\nMarker: []\n");
        var file = new LiveMarkerFileMetadata
        {
            UploaderId = "uploader", UploaderName = "作者", OriginalFileName = "记录.yaml",
            ContentHash = LiveMarkerFileStore.Hash(bytes), SourceTimeZoneId = "UTC"
        };
        file = file with { OriginId = file.Id };
        var store = new LiveMarkerFileStore(fixture.Paths);
        await store.PublishAsync(source.DirectoryPath, bytes, new(), file);
        var target = new BroadcastSession
        {
            DirectoryPath = "reconciliation/output", Title = "output", State = "Completed",
            StartedAtUtc = source.StartedAtUtc,
            EndedAtUtc = source.EndedAtUtc, GroupedSessionIds = [source.Id], ProjectionReady = false
        };
        var operation = new ProjectionOperation
            { SourceIds = [source.Id], Outputs = [new SessionManifest { Session = target }] };
        operation.LiveMarkerCopies.AddRange(await store.PlanCopiesAsync(source.DirectoryPath, target.Id,
            target.DirectoryPath, new HashSet<string>()));
        var journal = Path.Combine(fixture.StoragePath, ".work", "groups", "supervisor-marker-recovery",
            "operation.json");
        await JsonFiles.WriteAsync(journal, operation);
        await fixture.StartSupervisorAsync();
        await RecordingFixture.WaitUntilAsync(() => Task.FromResult(!File.Exists(journal)),
            "startup projection recovery under existing library lock", 5);
        Assert.Single(await store.ListAsync(target.DirectoryPath));
        Assert.True((await fixture.Context.Collection<BroadcastSession>("sessions")
            .Find(session => session.Id == source.Id).SingleAsync()).IsHidden);
        Assert.True((await fixture.Library.GetAsync(target.Id)).Session.ProjectionReady);
    }
}
