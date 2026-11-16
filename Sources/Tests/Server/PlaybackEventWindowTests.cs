using System.Text.Json.Nodes;
using Xunit;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证回放消息的时间窗口、投影和轻量响应。</summary>
public sealed class PlaybackEventWindowTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "recorder-playback-events-" + Guid.NewGuid().ToString("N"));

    /// <summary>回放响应保留消息和表情资料，原始协议文本继续完整保存在归档中。</summary>
    [Fact]
    public async Task PlaybackOmitsRawProtocolWithoutChangingTheArchive()
    {
        var rawJson = new string('中', 100000);
        await WriteAsync("session", new RecordedEventEnvelope
        {
            OffsetMs = 1000,
            Text = "回放弹幕",
            RawJson = rawJson,
            Payload = new() { ["emoticons"] = new JsonArray(new JsonObject { ["text"] = "[笑]", ["url"] = "https://example.test/smile.png" }) }
        });
        using var archiveReader = new EventArchiveReader();
        var reader = new SessionEventReader(archiveReader);

        var window = await reader.ReadPlaybackWindowAsync(Detail(), root, 2000, null, 500);

        var item = Assert.Single(window.Items);
        Assert.Empty(item.RawJson);
        Assert.Equal("回放弹幕", item.Text);
        Assert.Equal("[笑]", item.Payload!["emoticons"]![0]!["text"]!.GetValue<string>());
        var original = await reader.ReadAsync(Detail(), root, 0, 30000, null, null, 10);
        Assert.Equal(rawJson, Assert.Single(original.Items).RawJson);
    }

    /// <summary>历史保留最近消息，未来消息按时间排序并保留乱序追加记录。</summary>
    [Fact]
    public async Task PlaybackRetainsRequestedHistoryAndOrderedFutureMessages()
    {
        await WriteAsync("session",
            new() { OffsetMs = 1000, Text = "更早" },
            new() { OffsetMs = 2000, Text = "最近" },
            new() { OffsetMs = 6000, Text = "未来" },
            new() { OffsetMs = 3000, Text = "迟到记录" },
            new() { OffsetMs = 40000, Text = "窗口外" });

        using var archiveReader = new EventArchiveReader();
        var window = await new SessionEventReader(archiveReader).ReadPlaybackWindowAsync(Detail(), root, 2000, null, 1);

        Assert.Equal(2000, window.FromMs);
        Assert.Equal(32000, window.ToMs);
        Assert.Equal(["最近", "迟到记录", "未来"], window.Items.Select(item => item.Text));
        Assert.Equal([2L, 4L, 3L], window.Items.Select(item => item.Sequence));
    }

    /// <summary>复用归档索引时，各回放场次使用独立的时间和序号投影。</summary>
    [Fact]
    public async Task GroupedPlaybackDoesNotMutateTheSharedArchiveOrAnotherProjection()
    {
        await WriteAsync("source", new RecordedEventEnvelope { OffsetMs = 2000, Text = "来源弹幕", RawJson = "完整原文" });
        using var archiveReader = new EventArchiveReader();
        var reader = new SessionEventReader(archiveReader);
        var first = Detail();
        first.Session.EventSources = [new() { DirectoryPath = "source", StartOffsetMs = 1000 }];
        var second = Detail();
        second.Session.EventSources = [new() { DirectoryPath = "source", StartOffsetMs = 5000 }];

        var windows = await Task.WhenAll(
            reader.ReadPlaybackWindowAsync(first, root, 10000, null, 500),
            reader.ReadPlaybackWindowAsync(second, root, 10000, null, 500));

        Assert.Equal(3000, Assert.Single(windows[0].Items).OffsetMs);
        Assert.Equal(7000, Assert.Single(windows[1].Items).OffsetMs);
        Assert.All(windows, window => Assert.Equal(1_000_000_000_001L, Assert.Single(window.Items).Sequence));
        var original = await archiveReader.ReadAsync(Path.Combine(root, "source", "弹幕"), 0, 10000, null, null, 10);
        var item = Assert.Single(original.Items);
        Assert.Equal(2000, item.OffsetMs);
        Assert.Equal(1, item.Sequence);
        Assert.Equal("完整原文", item.RawJson);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    private static SessionDetail Detail() => new()
    {
        Session = new() { DirectoryPath = "session", EndedAtUtc = DateTime.UtcNow },
        DurationMs = 60000
    };

    private async Task WriteAsync(string directory, params RecordedEventEnvelope[] items)
    {
        await using var writer = new SessionEventWriter(Path.Combine(root, directory, "弹幕"));
        foreach (var item in items)
        {
            await writer.WriteAsync(item);
        }
    }
}
