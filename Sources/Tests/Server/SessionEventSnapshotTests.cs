using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证浏览器事件快照的完整读取、追加游标及来源失效。</summary>
public sealed class SessionEventSnapshotTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "recorder-event-snapshot-" + Guid.NewGuid().ToString("N"));
    private readonly EventArchiveReader archiveReader = new();
    private SessionEventSnapshotService Service { get; }

    /// <summary>为每个用例建立独立的归档与游标缓存。</summary>
    public SessionEventSnapshotTests()
    {
        Service = new(archiveReader);
    }

    /// <summary>完整快照突破旧分页上限，保留展示负载并释放原始协议文本。</summary>
    [Fact]
    public async Task FullSnapshotIncludesAllDisplayEventsWithoutChangingArchive()
    {
        var events = Enumerable.Range(0, 1005).Select(index => new RecordedEventEnvelope
        {
            OffsetMs = index,
            Text = index == 1004 ? "【同传】" : "普通弹幕",
            RawJson = index == 1004 ? new string('中', 100000) : "原始协议",
            Payload = new() { ["replyUserName"] = "回复对象", ["emoticons"] = new JsonArray(new JsonObject { ["text"] = "[笑]" }) }
        }).ToList();
        events.Add(new() { OffsetMs = 1006, Kind = "Raw", Text = "内部协议" });
        events.Add(new() { OffsetMs = 1007, Kind = "Gift", Text = "礼物" });
        await WriteAsync("session", events.ToArray());
        var original = await File.ReadAllTextAsync(ArchivePath("session"));

        var result = await Service.ReadAsync(Detail(), root, "【");

        var snapshot = Assert.IsType<SessionEventSnapshot>(result.Snapshot);
        Assert.True(snapshot.Reset);
        Assert.False(snapshot.IsLive);
        Assert.Equal(1006, snapshot.Items.Count);
        Assert.All(snapshot.Items, item => Assert.Empty(item.RawJson));
        Assert.Equal("回复对象", snapshot.Items[1004].Payload!["replyUserName"]!.GetValue<string>());
        Assert.Equal([1005L], snapshot.TranslationSequences);
        Assert.Equal(original, await File.ReadAllTextAsync(ArchivePath("session")));
        Assert.Equal(64, snapshot.Version.Length);
    }

    /// <summary>相同归档支持条件验证，游标校验仍可返回无需合并的空增量。</summary>
    [Fact]
    public async Task UnchangedSnapshotSupportsEntityTagAndEmptyDelta()
    {
        await WriteAsync("session", new RecordedEventEnvelope { OffsetMs = 100, Text = "【同传】" });
        var initial = await Service.ReadAsync(Detail(), root, "【");

        var unchanged = await Service.ReadAsync(Detail(), root, "【", initial.Snapshot!.Cursor, initial.EntityTag);
        var delta = await Service.ReadAsync(Detail(), root, "【", initial.Snapshot.Cursor);

        Assert.True(unchanged.NotModified);
        Assert.Null(unchanged.Snapshot);
        Assert.Equal(initial.EntityTag, unchanged.EntityTag);
        Assert.False(delta.Snapshot!.Reset);
        Assert.Empty(delta.Snapshot.Items);
        Assert.Empty(delta.Snapshot.TranslationSequences);
        Assert.Equal(initial.Snapshot.Version, delta.Snapshot.Version);
        Assert.Equal(initial.Snapshot.Cursor, delta.Snapshot.Cursor);
    }

    /// <summary>直播保留已提交的未来偏移，增长的播放时长不会漏掉低序号事件。</summary>
    [Fact]
    public async Task LiveCursorRetainsFutureOffsetsAndOnlyReadsAppendedEvents()
    {
        var detail = Detail(true, 1000);
        await WriteAsync("session", new RecordedEventEnvelope { OffsetMs = 20000, Text = "【未来同传】" });
        var first = await Service.ReadAsync(detail, root, "【");
        Assert.Equal(20000, Assert.Single(first.Snapshot!.Items).OffsetMs);
        detail.DurationMs = 30000;
        await WriteAsync("session", new RecordedEventEnvelope { OffsetMs = 500, Text = "迟到弹幕" });

        var delta = await Service.ReadAsync(detail, root, "【", first.Snapshot.Cursor);

        Assert.False(delta.Snapshot!.Reset);
        Assert.True(delta.Snapshot.IsLive);
        Assert.Equal(first.Snapshot.Version, delta.Snapshot.Version);
        Assert.Equal(2, Assert.Single(delta.Snapshot.Items).Sequence);
        Assert.Equal("迟到弹幕", delta.Snapshot.Items[0].Text);
        Assert.Empty(delta.Snapshot.TranslationSequences);
    }

    /// <summary>只提交完整换行记录，半条追加不会改变快照或推进到尚未完成的记录。</summary>
    [Fact]
    public async Task PartialLineDoesNotAdvanceCursorUntilCommitted()
    {
        await WriteAsync("session", new RecordedEventEnvelope { OffsetMs = 100, Text = "第一条" });
        var first = await Service.ReadAsync(Detail(true), root, "【");
        var line = JsonSerializer.Serialize(new RecordedEventEnvelope { Sequence = 2, OffsetMs = 200, Text = "【第二条】" }, JsonFiles.Options);
        await File.AppendAllTextAsync(ArchivePath("session"), line);

        var incomplete = await Service.ReadAsync(Detail(true), root, "【", first.Snapshot!.Cursor, first.EntityTag);
        Assert.True(incomplete.NotModified);
        await File.AppendAllTextAsync(ArchivePath("session"), "\n");
        var completed = await Service.ReadAsync(Detail(true), root, "【", first.Snapshot.Cursor);

        Assert.False(completed.Snapshot!.Reset);
        Assert.Equal(2, Assert.Single(completed.Snapshot.Items).Sequence);
        Assert.Equal([2L], completed.Snapshot.TranslationSequences);
    }

    /// <summary>投影沿用稳定来源序号、来源裁切及已结束播放的右开边界。</summary>
    [Fact]
    public async Task ProjectionPreservesSequenceIdentityAndPlaybackBoundaries()
    {
        await WriteAsync("source", new() { OffsetMs = 999, Text = "来源之前" },
            new() { OffsetMs = 1000, Text = "【起点】" },
            new() { OffsetMs = 1999, Text = "末条" },
            new() { OffsetMs = 2000, Text = "【播放终点】" });
        var detail = Detail(false, 1000);
        detail.Session.EventSources = [new() { DirectoryPath = "source", FromMs = 1000, ToMs = 3000, StartOffsetMs = -1000 }];

        var result = await Service.ReadAsync(detail, root, "【");

        Assert.Equal([0d, 999d], result.Snapshot!.Items.Select(item => item.OffsetMs));
        Assert.Equal([1_000_000_000_002L, 1_000_000_000_003L], result.Snapshot.Items.Select(item => item.Sequence));
        Assert.Equal([1_000_000_000_002L], result.Snapshot.TranslationSequences);
        Assert.Equal(1000, (await archiveReader.ReadAsync(Path.GetDirectoryName(ArchivePath("source"))!, 0, 3000, null, null, 10)).Items[1].OffsetMs);
    }

    /// <summary>同一归档在多个来源中分别追加，不能用单一最大序号遗漏较早来源的新消息。</summary>
    [Fact]
    public async Task GroupedDeltaTracksEverySourceIndependently()
    {
        await WriteAsync("first", new RecordedEventEnvelope { OffsetMs = 100, Text = "首来源" });
        await WriteAsync("second", new RecordedEventEnvelope { OffsetMs = 100, Text = "次来源" });
        var detail = Detail(true);
        detail.Session.EventSources = [new() { DirectoryPath = "first" }, new() { DirectoryPath = "second", StartOffsetMs = 1000 }];
        var first = await Service.ReadAsync(detail, root, "【");
        await WriteAsync("first", new RecordedEventEnvelope { OffsetMs = 200, Text = "【首来源追加】" });

        var delta = await Service.ReadAsync(detail, root, "【", first.Snapshot!.Cursor);

        Assert.False(delta.Snapshot!.Reset);
        Assert.Equal(1_000_000_000_002L, Assert.Single(delta.Snapshot.Items).Sequence);
        Assert.Equal([1_000_000_000_002L], delta.Snapshot.TranslationSequences);
    }

    /// <summary>中部同长改写即使保留文件时间戳，也必须让旧游标失效。</summary>
    [Fact]
    public async Task SameLengthMiddleRewriteWithPreservedTimestampResetsSnapshot()
    {
        await WriteAsync("session", Enumerable.Range(0, 300).Select(index => new RecordedEventEnvelope
        {
            OffsetMs = index,
            Text = index == 150 ? "before" : new string('x', 100)
        }).ToArray());
        var first = await Service.ReadAsync(Detail(true), root, "【");
        var path = ArchivePath("session");
        var modified = File.GetLastWriteTimeUtc(path);
        var original = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, original.Replace("before", "after!", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(path, modified);

        var updated = await Service.ReadAsync(Detail(true), root, "【", first.Snapshot!.Cursor, first.EntityTag);

        Assert.False(updated.NotModified);
        Assert.True(updated.Snapshot!.Reset);
        Assert.NotEqual(first.Snapshot.Version, updated.Snapshot.Version);
        Assert.Contains(updated.Snapshot.Items, item => item.Text == "after!");
    }

    /// <summary>规则、来源投影、媒体版本和结束边界改变时重新交付完整快照。</summary>
    [Theory]
    [InlineData("pattern")]
    [InlineData("projection")]
    [InlineData("media")]
    [InlineData("completion")]
    public async Task ContextChangesResetTheSnapshot(string change)
    {
        await WriteAsync("session", new() { OffsetMs = 100, Text = "【同传】" }, new() { OffsetMs = 600, Text = "JP: translation" });
        var detail = Detail(true, 1000);
        var initial = await Service.ReadAsync(detail, root, "【");
        var pattern = "【";
        if (change == "pattern")
        {
            pattern = "^JP:";
        }
        else if (change == "projection")
        {
            detail.Session.EventSources = [new() { DirectoryPath = "session", StartOffsetMs = 10 }];
        }
        else if (change == "media")
        {
            detail.Session.MediaRevision++;
        }
        else
        {
            detail.Session.EndedAtUtc = detail.Session.StartedAtUtc.AddMilliseconds(1000);
            detail.DurationMs = 500;
        }

        var result = await Service.ReadAsync(detail, root, pattern, initial.Snapshot!.Cursor, initial.EntityTag);

        Assert.True(result.Snapshot!.Reset);
        Assert.NotEqual(initial.Snapshot.Version, result.Snapshot.Version);
        Assert.NotEmpty(result.Snapshot.Items);
        Assert.Equal(change == "pattern" ? 2L : change == "projection" ? 1_000_000_000_001L : 1L, Assert.Single(result.Snapshot.TranslationSequences));
        Assert.Equal(change == "completion" ? 1 : 2, result.Snapshot.Items.Count);
    }

    /// <summary>损坏游标可恢复全量，归档截断不能把旧缓存误判为可追加。</summary>
    [Fact]
    public async Task InvalidCursorAndTruncationRecoverWithAFullSnapshot()
    {
        await WriteAsync("session", new() { OffsetMs = 100, Text = "第一条" }, new() { OffsetMs = 200, Text = "第二条" });
        var initial = await Service.ReadAsync(Detail(true), root, "【");
        var invalid = await Service.ReadAsync(Detail(true), root, "【", "invalid-cursor", initial.EntityTag);
        Assert.True(invalid.Snapshot!.Reset);
        Assert.Equal(2, invalid.Snapshot.Items.Count);
        var lines = await File.ReadAllLinesAsync(ArchivePath("session"));
        await File.WriteAllTextAsync(ArchivePath("session"), lines[0] + "\n");

        var truncated = await Service.ReadAsync(Detail(true), root, "【", initial.Snapshot!.Cursor);

        Assert.True(truncated.Snapshot!.Reset);
        Assert.Single(truncated.Snapshot.Items);
        Assert.NotEqual(initial.Snapshot.Version, truncated.Snapshot.Version);
    }

    /// <summary>已撤销的场次可访问状态在条件验证前检查。</summary>
    [Fact]
    public async Task HiddenSessionCannotReuseAnExistingEntityTag()
    {
        await WriteAsync("session", new RecordedEventEnvelope { OffsetMs = 100, Text = "第一条" });
        var detail = Detail();
        var initial = await Service.ReadAsync(detail, root, "【");
        detail.Session.IsHidden = true;

        var error = await Assert.ThrowsAsync<InfrastructureException>(() => Service.ReadAsync(detail, root, "【", initial.Snapshot!.Cursor, initial.EntityTag));

        Assert.Equal(404, error.StatusCode);
    }

    /// <summary>很多投影来源仍交付有界长度游标，元数据淘汰或服务重启时可安全恢复全量。</summary>
    [Fact]
    public async Task ManySourcesUseABoundedCursorAndRecoverAfterMetadataLoss()
    {
        await WriteAsync("session", new RecordedEventEnvelope { OffsetMs = 100, Text = "【共享来源】" });
        var detail = Detail(true);
        detail.Session.EventSources = Enumerable.Range(0, 100)
            .Select(index => new EventArchiveSource { DirectoryPath = "session", StartOffsetMs = index * 1000 }).ToList();
        var initial = await Service.ReadAsync(detail, root, "【");
        Assert.InRange(initial.Snapshot!.Cursor.Length, 1, 4096);
        Assert.StartsWith("r.", initial.Snapshot.Cursor);
        Assert.Equal(100, initial.Snapshot.Items.Count);

        var resumed = await Service.ReadAsync(detail, root, "【", initial.Snapshot.Cursor);
        using var restarted = new SessionEventSnapshotService(archiveReader);
        var recovered = await restarted.ReadAsync(detail, root, "【", resumed.Snapshot!.Cursor);

        Assert.False(resumed.Snapshot.Reset);
        Assert.Empty(resumed.Snapshot.Items);
        Assert.True(recovered.Snapshot!.Reset);
        Assert.Equal(100, recovered.Snapshot.Items.Count);
    }

    /// <summary>同传灾难回溯只使同传匹配失败，不得阻断原始弹幕或留下不完整匹配结果。</summary>
    [Fact]
    public async Task TranslationFailureDoesNotBlockEventsAndPersistsAcrossDeltas()
    {
        await WriteAsync("session", new() { OffsetMs = 100, Text = "aaaa" },
            new() { OffsetMs = 200, Text = new string('a', 100000) + "!" },
            new() { OffsetMs = 300, Text = "普通弹幕" });
        var initial = await Service.ReadAsync(Detail(true), root, "(a+)+$");
        Assert.Equal(3, initial.Snapshot!.Items.Count);
        Assert.Empty(initial.Snapshot.TranslationSequences);
        Assert.Equal("TRANSLATION_DANMU_PATTERN_TIMEOUT", initial.Snapshot.TranslationError);
        var empty = await Service.ReadAsync(Detail(true), root, "(a+)+$", initial.Snapshot.Cursor);
        Assert.False(empty.Snapshot!.Reset);
        Assert.Empty(empty.Snapshot.Items);
        Assert.Equal(initial.Snapshot.TranslationError, empty.Snapshot.TranslationError);
        await WriteAsync("session", new RecordedEventEnvelope { OffsetMs = 400, Text = "aaaa" });
        var delta = await Service.ReadAsync(Detail(true), root, "(a+)+$", empty.Snapshot.Cursor);
        Assert.Single(delta.Snapshot!.Items);
        Assert.Empty(delta.Snapshot.TranslationSequences);
        Assert.Equal(initial.Snapshot.TranslationError, delta.Snapshot.TranslationError);

        var recovered = await Service.ReadAsync(Detail(true), root, "^a+$", delta.Snapshot.Cursor);

        Assert.True(recovered.Snapshot!.Reset);
        Assert.Equal(4, recovered.Snapshot.Items.Count);
        Assert.Null(recovered.Snapshot.TranslationError);
        Assert.Equal([1L, 4L], recovered.Snapshot.TranslationSequences);
    }

    /// <summary>无法编译的已保存规则仅暴露同传错误，完整和空增量仍可读取弹幕。</summary>
    [Fact]
    public async Task InvalidTranslationPatternOnlyAffectsTranslationResults()
    {
        await WriteAsync("session", new RecordedEventEnvelope { OffsetMs = 100, Text = "【同传】" });

        var initial = await Service.ReadAsync(Detail(), root, "[");
        var delta = await Service.ReadAsync(Detail(), root, "[", initial.Snapshot!.Cursor);
        var full = await Service.ReadAsync(Detail(), root, "[");

        Assert.Single(initial.Snapshot.Items);
        Assert.Empty(initial.Snapshot.TranslationSequences);
        Assert.Equal("TRANSLATION_DANMU_PATTERN_INVALID", initial.Snapshot.TranslationError);
        Assert.Empty(delta.Snapshot!.Items);
        Assert.Equal(initial.Snapshot.TranslationError, delta.Snapshot.TranslationError);
        Assert.True(full.Snapshot!.Reset);
        Assert.Single(full.Snapshot.Items);
        Assert.Equal(initial.Snapshot.TranslationError, full.Snapshot.TranslationError);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Service.Dispose();
        archiveReader.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    private static SessionDetail Detail(bool live = false, double durationMs = 60000) => new()
    {
        Session = new()
        {
            Id = "session-id", DirectoryPath = "session", StartedAtUtc = DateTime.UnixEpoch,
            EndedAtUtc = live ? null : DateTime.UnixEpoch.AddMilliseconds(durationMs)
        },
        DurationMs = durationMs
    };

    private string ArchivePath(string directory) => Path.Combine(root, directory, "弹幕", "事件.jsonl");

    private async Task WriteAsync(string directory, params RecordedEventEnvelope[] items)
    {
        await using var writer = new SessionEventWriter(Path.GetDirectoryName(ArchivePath(directory))!);
        foreach (var item in items)
        {
            await writer.WriteAsync(item);
        }
    }
}
