using Xunit;
using Yohuke.LiveRecorder.Domain.LiveMarkers;
using Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证浏览器记录缓存随内容、解析规则和录像投影失效。</summary>
public sealed class LiveMarkerPreviewRevisionTests
{
    /// <summary>同一不可变记录在不同读取中保持稳定，显示归属不影响条目版本。</summary>
    [Fact]
    public void UnchangedDocumentAndProjectionKeepStableRevision()
    {
        var file = new LiveMarkerFileMetadata
        {
            ContentHash = new('A', 64),
            SourceTimeZoneId = "Asia/Shanghai",
            CalibrationMs = 250,
            EntryCount = 10
        };
        var startedAt = new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);
        var revision = LiveMarkerPreviewRevision.Create(file, startedAt);
        Assert.Equal(64, revision.Length);
        Assert.Equal(revision, LiveMarkerPreviewRevision.Create(file with { UploaderName = "新名称" }, startedAt));
    }

    /// <summary>源内容、校准、解析时区及录像起点变化使缓存失效。</summary>
    [Fact]
    public void DocumentOrProjectionChangesInvalidateRevision()
    {
        var file = new LiveMarkerFileMetadata
        {
            ContentHash = new('A', 64),
            SourceTimeZoneId = "Asia/Shanghai",
            EntryCount = 10
        };
        var startedAt = new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);
        var revision = LiveMarkerPreviewRevision.Create(file, startedAt);
        foreach (var changed in new[]
                 {
                     file with { ContentHash = new('B', 64) },
                     file with { CalibrationMs = -250 },
                     file with { SourceTimeZoneId = "UTC" },
                     file with { EntryCount = 11 }
                 })
        {
            Assert.NotEqual(revision, LiveMarkerPreviewRevision.Create(changed, startedAt));
        }
        Assert.NotEqual(revision, LiveMarkerPreviewRevision.Create(file, startedAt.AddMilliseconds(1)));
    }
}
