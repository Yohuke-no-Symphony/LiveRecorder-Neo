using System.Reflection;
using System.Text;
using Yohuke.DanmuCore.Apis;
using Yohuke.DanmuCore.Apis.Models.Bilibili;
using Yohuke.DanmuCore.Services;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>确保新增原始旁路不会改变旧消费者行为。</summary>
public sealed class RawEventCompatibilityTests
{
    /// <summary>平台上下播命令经过实际Socket解析旁路后保留为可识别的房间状态事件。</summary>
    [Theory]
    [InlineData("LIVE")]
    [InlineData("PREPARING")]
    public void BroadcastLifecycleCommandsReachTheRecordingEventMapper(string command)
    {
        using var socket = new BilibiliLiveWebSocket(100, "");
        var events = new List<Yohuke.LiveRecorder.Domain.RecordedEventEnvelope>();
        socket.RawBusinessEventReceived += (_, value) => events.AddRange(BilibiliEventMapper.Map(value));

        Feed(socket, "{\"cmd\":\"" + command + "\",\"roomid\":100}");

        var item = Assert.Single(events);
        Assert.Equal("RoomState", item.Kind);
        Assert.Equal(command, item.Command);
        Assert.Equal(DateTimeKind.Utc, item.ReceivedAtUtc.Kind);
    }

    [Fact]
    public void SameSocketPublishesRawBeforeExistingTypedEvent()
    {
        using var socket = new BilibiliLiveWebSocket(100, "");
        var order = new List<string>();
        ((IBilibiliRawEventSource)socket).RawBusinessEventReceived += (_, value) => order.Add(value.Command);
        ((ILiveDanmuSocket)socket).DanmuReceived += (_, value) => order.Add(value.Content);
        Feed(socket, """{"cmd":"DANMU_MSG","info":[[],"你好",[42,"用户"]]}""");
        Assert.Equal(new[] { "DANMU_MSG", "你好" }, order);
    }

    [Fact]
    public void UnknownCommandsArePreservedAndObserverFailureDoesNotBreakLegacyEvents()
    {
        using var socket = new BilibiliLiveWebSocket(100, "");
        var raw = new List<string>();
        var danmu = 0;
        socket.RawBusinessEventReceived += (_, value) => raw.Add(value.Command);
        socket.RawBusinessEventReceived += (_, _) => throw new IOException("Simulated observer failure.");
        socket.DanmuReceived += (_, _) => danmu++;
        Feed(socket, """{"cmd":"FUTURE_EVENT","data":{"amount":42}}""");
        Feed(socket, """{"cmd":"DANMU_MSG","info":[[],"你好",[42,"用户"]]}""");
        Assert.Equal(new[] { "FUTURE_EVENT", "DANMU_MSG" }, raw);
        Assert.Equal(1, danmu);
    }

    [Fact]
    public void ArchivedDanmuKeepsSourceMillisecondsAndOriginalPayload()
    {
        const string json = """{"cmd":"DANMU_MSG","msg_id":"message-1","info":[[0,0,0,0,1800000000123],"内容",[42,"用户"]]}""";
        var value = Assert.Single(BilibiliEventMapper.Map(new("100", "DANMU_MSG", json, DateTimeOffset.UtcNow)));
        Assert.Equal("Danmu", value.Kind);
        Assert.Equal("42", value.UserId);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1800000000123).UtcDateTime, value.SourceTimeUtc);
        Assert.Equal(json, value.RawJson);
    }

    [Fact]
    public void UnknownEventRemainsReprocessableWithoutInventingMoney()
    {
        var value = Assert.Single(BilibiliEventMapper.Map(new("100", "FUTURE_EVENT", """{"cmd":"FUTURE_EVENT","data":{"amount":42}}""", DateTimeOffset.UtcNow)));
        Assert.Equal("Raw", value.Kind);
        Assert.Null(value.Amount);
        Assert.Contains("FUTURE_EVENT", value.RawJson);
    }

    [Fact]
    public void LegacyDanmuWithoutTimestampStillProducesVisibleMessage()
    {
        var value = Assert.Single(BilibiliEventMapper.Map(new("100", "DANMU_MSG", """{"cmd":"DANMU_MSG","info":[[],"你好",[42,"用户"]]}""", DateTimeOffset.UtcNow)));
        Assert.Equal("Danmu", value.Kind);
        Assert.Equal("你好", value.Text);
        Assert.Null(value.SourceTimeUtc);
    }

    private static void Feed(BilibiliLiveWebSocket socket, string json) => typeof(BilibiliLiveWebSocket).GetMethod("AnalyseBusinessPackage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(socket, [Encoding.UTF8.GetBytes(json)]);
}
