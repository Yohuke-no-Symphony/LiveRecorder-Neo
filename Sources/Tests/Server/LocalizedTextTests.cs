using System.Globalization;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证共享错误标识、翻译完整性与历史诊断兼容。</summary>
public sealed class LocalizedTextTests
{
    /// <summary>所有公开系统消息均具备三语资源且保留格式参数。</summary>
    [Fact]
    public void EverySystemMessageHasCompleteTranslations()
    {
        foreach (var code in Enum.GetValues<MessageCode>())
        {
            var name = code.ToString();
            var arguments = Enumerable.Range(0, 10).Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray();
            var chinese = LocalizedText.Format(name, name, arguments, CultureInfo.GetCultureInfo("zh-CN"));
            var english = LocalizedText.Format(name, name, arguments, CultureInfo.GetCultureInfo("en-US"));
            var japanese = LocalizedText.Format(name, name, arguments, CultureInfo.GetCultureInfo("ja-JP"));
            Assert.NotEqual(name, chinese);
            Assert.NotEqual(chinese, english);
            Assert.NotEqual(chinese, japanese);
        }
    }

    /// <summary>异常的诊断保持中文，而响应可按不同用户的请求文化格式化。</summary>
    [Fact]
    public void ParameterizedFailureKeepsStableCodeAndOriginalDiagnostic()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            var error = new InfrastructureException(MessageCode.BILIBILI_ROOM_UNAVAILABLE, 502, -400);
            Assert.Equal("无法读取直播间资料（-400）。", error.Message);
            Assert.Equal("BILIBILI_ROOM_UNAVAILABLE", error.Code);
            Assert.Equal(["-400"], error.Arguments);
            Assert.Equal("配信ルームの情報を取得できませんでした（-400）。", LocalizedText.Format(error.Code, error.Message, error.Arguments));
            Assert.Equal("The live room profile could not be retrieved (-400).", LocalizedText.Format(error.Code, error.Message, error.Arguments, CultureInfo.GetCultureInfo("en-US")));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>历史记录和第三方诊断无错误码时原样返回。</summary>
    [Fact]
    public void HistoricalAndUnknownDiagnosticsRemainUnchanged()
    {
        const string DIAGNOSTIC = "ffmpeg: unknown codec {0}";
        Assert.Equal(DIAGNOSTIC, LocalizedText.Format(null, DIAGNOSTIC));
        Assert.Equal(DIAGNOSTIC, LocalizedText.Format("FUTURE_CODE", DIAGNOSTIC, ["x"]));
        Assert.Equal(DIAGNOSTIC, new InfrastructureException(DIAGNOSTIC).Message);
    }
}
