using System.Globalization;
using Xunit;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Server.Localization;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证共享状态按读取语言格式化，并保留原始业务内容。</summary>
public sealed class LocalizedStateTests
{
    /// <summary>同一错误标识可以在后续请求中切换语言。</summary>
    [Fact]
    public void StoredMessageCodeCanBeRenderedForDifferentReaders()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            var task = new MediaJob
            {
                Title = "原始直播标题",
                Error = "无法读取直播间资料（-400）。",
                ErrorCode = MessageCode.BILIBILI_ROOM_UNAVAILABLE.ToString(),
                ErrorArguments = ["-400"]
            };
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("The live room profile could not be retrieved (-400).", LocalizedState.ForResponse(task).Error);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            Assert.Equal("配信ルームの情報を取得できませんでした（-400）。", LocalizedState.ForResponse(task).Error);
            Assert.Equal("原始直播标题", task.Title);
            var historical = new Streamer { Error = "external diagnostic {0}" };
            Assert.Equal("external diagnostic {0}", LocalizedState.ForResponse(historical).Error);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>文件事件中的用户文本保持原文，只有系统中断提示参与翻译。</summary>
    [Fact]
    public void EventTranslationPreservesOriginalComments()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var page = new EventPage
            {
                Items =
                [
                    new() { Kind = "Danmu", UserName = "夜半", Text = "弹幕连接中断", RawJson = "original" },
                    new() { Kind = "Gap", Text = "弹幕连接中断", TextCode = MessageCode.RECORDING_EVENT_CONNECTION_LOST.ToString() }
                ]
            };
            LocalizedState.ForResponse(page);
            Assert.Equal("弹幕连接中断", page.Items[0].Text);
            Assert.Equal("夜半", page.Items[0].UserName);
            Assert.Equal("original", page.Items[0].RawJson);
            Assert.Equal("Event connection interrupted", page.Items[1].Text);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
