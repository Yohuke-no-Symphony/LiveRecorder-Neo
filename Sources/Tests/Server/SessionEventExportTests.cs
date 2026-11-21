using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证文本下载完整保留互动信息和场次时间投影。</summary>
public sealed class SessionEventExportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"live-recorder-export-tests-{Guid.NewGuid():N}");
    private readonly EventArchiveReader archive = new();
    private readonly CultureInfo previousCulture = CultureInfo.CurrentUICulture;

    /// <summary>构造独立归档并固定验证文件语言。</summary>
    public SessionEventExportTests()
    {
        Directory.CreateDirectory(root);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
    }

    /// <summary>整场下载跨过分页上限，保留归档中的互动类型和迟到事件。</summary>
    [Fact]
    public async Task FullExportIncludesReadableInteractionsPastThePageLimit()
    {
        var start = new DateTime(2026, 11, 21, 12, 30, 0, DateTimeKind.Utc);
        var session = new BroadcastSession { DirectoryPath = "session", Title = "测试/场次", StartedAtUtc = start, EndedAtUtc = start.AddSeconds(10) };
        var directory = Path.Combine(root, session.DirectoryPath, "弹幕");
        await using (var writer = new SessionEventWriter(directory))
        {
            for (var index = 0; index < 1005; index++)
            {
                await writer.WriteAsync(new() { OffsetMs = 1000 + index, UserName = "观众", Text = $"普通弹幕 {index}" });
            }
            await writer.WriteAsync(new() { Kind = "SuperChat", OffsetMs = 500, Text = "留言正文", UserName = "观众", Amount = 30 });
            await writer.WriteAsync(new() { Kind = "Gift", OffsetMs = 501, Payload = new() { ["giftName"] = "花" }, Quantity = 3, CoinType = "silver" });
            await writer.WriteAsync(new() { Kind = "Guard", OffsetMs = 502, Text = "舰长", Quantity = 2, Amount = 198m, Command = "GUARD_BUY" });
            await writer.WriteAsync(new() { Kind = "RedPocket", OffsetMs = 503, TransactionId = "777", UserName = "送出者", Text = "来抽奖" });
            await writer.WriteAsync(new()
            {
                Kind = "RedPocket", OffsetMs = 2500, TransactionId = "777", UserName = "送出者",
                Payload = JsonNode.Parse("""
                    {"isDrawn":true,"redPocket":{"hasResult":true,"totalBattery":10,"totalAwardCount":2,
                        "awards":[{"name":"花","giftId":"2","quantity":2}],
                        "winners":[{"uid":42,"userName":"中奖用户","giftId":"2","quantity":1},
                            {"uid":99,"userName":"隐藏姓名","isMystery":true,"batteryAmount":5}]}}
                    """)!.AsObject()
            });
            await writer.WriteAsync(new() { Kind = "Raw", OffsetMs = 2501, Text = "原始协议" });
        }
        await File.AppendAllTextAsync(Path.Combine(directory, "事件.jsonl"), """{"kind":"Danmu","text":"未提交尾行","offsetMs":3000}""");

        var (content, fileName) = await new SessionEventExportService(archive).ExportAsync(session, root);
        await using var file = content;
        using var reader = new StreamReader(file);
        var text = await reader.ReadToEndAsync();

        Assert.Equal("测试_场次_20261121_123000Z_弹幕.txt", fileName);
        Assert.Contains("场次开始 2026-11-21 12:30:00 UTC", text);
        Assert.Contains("[00:00:00.500] 【SC】 观众 · ¥30 · 留言正文", text);
        Assert.Contains("花 ×3 · 银瓜子礼物", text);
        Assert.Contains("舰长 ×2 · 参考金额 ¥198", text);
        Assert.Contains("[00:00:00.503] 【红包】 送出者 · 红包编号 777 · 待开奖 · 来抽奖", text);
        Assert.Contains("[00:00:02.500] 【红包】 送出者 · 红包编号 777 · 已开奖 · 总电池 10 · 奖品总数 2", text);
        Assert.Contains("奖品 花 ×2", text);
        Assert.Contains("中奖用户 (UID 42) · 花 ×1", text);
        Assert.Contains("神秘人 · 电池 5", text);
        Assert.Equal(1005, text.Split('\n').Count(line => line.Contains("【弹幕】", StringComparison.Ordinal)));
        Assert.Contains("[00:00:02.004] 【弹幕】 观众 · 普通弹幕 1004", text);
        Assert.True(text.IndexOf("留言正文", StringComparison.Ordinal) < text.IndexOf("普通弹幕 0", StringComparison.Ordinal));
        Assert.DoesNotContain("隐藏姓名", text);
        Assert.DoesNotContain("UID 99", text);
        Assert.DoesNotContain("原始协议", text);
        Assert.DoesNotContain("未提交尾行", text);
    }

    /// <summary>同传按原始正文筛选，拆分边界和场次间隔按投影时间保留。</summary>
    [Fact]
    public async Task TranslationExportUsesTheConfiguredPatternAndProjectedTimeline()
    {
        var session = new BroadcastSession
        {
            EventSources =
            [
                new() { DirectoryPath = "first", FromMs = 1000, ToMs = 2000, StartOffsetMs = -1000 },
                new() { DirectoryPath = "second", StartOffsetMs = 3660000 }
            ]
        };
        await using (var writer = new SessionEventWriter(Path.Combine(root, "first", "弹幕")))
        {
            await writer.WriteAsync(new() { OffsetMs = 999, Text = "译：前一个切分" });
            await writer.WriteAsync(new() { OffsetMs = 1500, Text = "译：切分后续", UserName = "译者" });
            await writer.WriteAsync(new() { OffsetMs = 1000, Text = "译：切分起点", UserName = "译者" });
            await writer.WriteAsync(new() { OffsetMs = 1500, Text = "【其他规则】" });
            await writer.WriteAsync(new() { OffsetMs = 1500, Kind = "SuperChat", Text = "译：SC" });
            await writer.WriteAsync(new() { OffsetMs = 2000, Text = "译：下一个切分" });
        }
        await using (var writer = new SessionEventWriter(Path.Combine(root, "second", "弹幕")))
        {
            await writer.WriteAsync(new() { OffsetMs = 303.8, Text = "译：归组场次", UserName = "译者" });
        }
        var pattern = TranslationDanmuPattern.Create("^译：");
        var (content, fileName) = await new SessionEventExportService(archive).ExportAsync(session, root, pattern);
        await using var file = content;
        using var reader = new StreamReader(file);
        var text = await reader.ReadToEndAsync();

        Assert.EndsWith("_同传.txt", fileName);
        Assert.Equal(new[]
        {
            "[00:00:00.000] 译者 · 译：切分起点",
            "[00:00:00.500] 译者 · 译：切分后续",
            "[01:01:00.303] 译者 · 译：归组场次"
        }, text.Split('\n').Where(line => line.StartsWith('[')));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CultureInfo.CurrentUICulture = previousCulture;
        archive.Dispose();
        Directory.Delete(root, true);
    }
}
