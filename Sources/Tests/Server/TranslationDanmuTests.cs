using System.Text.Json;
using Xunit;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证同传匹配的原文语义及归组、拆分后的稳定分页。</summary>
public sealed class TranslationDanmuTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "live-recorder-translations-" + Guid.NewGuid().ToString("N"));

    /// <summary>正文以外的字段不能命中同传规则，重复正文和时间仍保留各自的事件。</summary>
    [Fact]
    public async Task MatchingUsesOnlyDanmuTextAndPreservesOriginalRepeatedMessages()
    {
        var session = new BroadcastSession { DirectoryPath = "source" };
        await using (var writer = Writer("source"))
        {
            await writer.WriteAsync(new() { Text = "普通弹幕", UserName = "【用户名】", RawJson = "【原始协议】" });
            await writer.WriteAsync(new() { Text = "【未闭合" });
            await writer.WriteAsync(new() { Text = "【其他类型】", Kind = "Gift" });
            await writer.WriteAsync(new() { Text = "译者【正文】后缀", OffsetMs = 100 });
            await writer.WriteAsync(new() { Text = "译者【正文】后缀", OffsetMs = 100 });
            await writer.WriteAsync(new() { Text = "【】", OffsetMs = 200 });
        }

        var result = await new SessionEventReader(new()).ReadAsync(session, root, 0, 1000, "Danmu", null, 100,
            textPattern: TranslationDanmuPattern.Create(new ServerSettings().TranslationDanmuPattern));

        Assert.Equal(new[] { "译者【正文】后缀", "译者【正文】后缀", "【】" }, result.Items.Select(item => item.Text));
        Assert.Equal(new long[] { 4, 5, 6 }, result.Items.Select(item => item.Sequence));
        Assert.Null(result.NextCursor);
    }

    /// <summary>分页跨越无匹配来源，保持来源序号和拆分偏移，并排除有效播放终点。</summary>
    [Fact]
    public async Task FilteredPagesKeepSourceCoordinatesAndExcludeSplitAndPlaybackBoundaries()
    {
        await using (var first = Writer("first"))
        {
            await first.WriteAsync(new() { Text = "【拆分之前】", OffsetMs = 99999 });
            await first.WriteAsync(new() { Text = "【第一来源】", OffsetMs = 100999 });
            await first.WriteAsync(new() { Text = "【拆分之后】", OffsetMs = 101000 });
        }
        await using (var empty = Writer("empty"))
        {
            await empty.WriteAsync(new() { Text = "没有同传", OffsetMs = 0 });
        }
        await using (var second = Writer("second"))
        {
            await second.WriteAsync(new() { Text = "【第二来源开始】", OffsetMs = 0 });
            await second.WriteAsync(new() { Text = "【第二来源结束】", OffsetMs = 999 });
            await second.WriteAsync(new() { Text = "【播放终点】", OffsetMs = 1000 });
        }
        var detail = new SessionDetail
        {
            DurationMs = 2000,
            Session = new()
            {
                EndedAtUtc = DateTime.UtcNow,
                EventSources =
                [
                    new() { DirectoryPath = "first", StartOffsetMs = -100000, FromMs = 100000, ToMs = 101000 },
                    new() { DirectoryPath = "empty", StartOffsetMs = 1000, FromMs = 0, ToMs = 1000 },
                    new() { DirectoryPath = "second", StartOffsetMs = 1000, FromMs = 0 }
                ]
            }
        };
        var originalSources = JsonSerializer.Serialize(detail.Session.EventSources);
        var collected = new List<RecordedEventEnvelope>();
        long? cursor = null;
        do
        {
            var result = await new SessionEventReader(new()).ReadAsync(detail, root, 0, 3000, "Danmu", cursor, 1,
                textPattern: TranslationDanmuPattern.Create("【.*?】"));
            collected.AddRange(result.Items);
            Assert.True(result.NextCursor is null || result.NextCursor > (cursor ?? 0));
            cursor = result.NextCursor;
        } while (cursor is not null);

        Assert.Equal(new[] { 999d, 1000d, 1999d }, collected.Select(item => item.OffsetMs));
        Assert.Equal(new[] { 1_000_000_000_002, 3_000_000_000_001, 3_000_000_000_002 }, collected.Select(item => item.Sequence));
        Assert.Equal(originalSources, JsonSerializer.Serialize(detail.Session.EventSources));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    private SessionEventWriter Writer(string directory) => new(Path.Combine(root, directory, "弹幕"));
}
