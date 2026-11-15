using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Server.Localization;

/// <summary>只在读取响应时本地化系统状态，保持数据库及归档中的诊断可重放。</summary>
public static class LocalizedState
{
    /// <summary>为当前请求格式化后台任务错误。</summary>
    public static MediaJob ForResponse(MediaJob value)
    {
        if (value.Error is not null)
        {
            value.Error = LocalizedText.Format(value.ErrorCode, value.Error, value.ErrorArguments);
        }
        return value;
    }

    /// <summary>为当前请求格式化录播场次错误。</summary>
    public static BroadcastSession ForResponse(BroadcastSession value)
    {
        if (value.Error is not null)
        {
            value.Error = LocalizedText.Format(value.ErrorCode, value.Error, value.ErrorArguments);
        }
        return value;
    }

    /// <summary>为当前请求格式化录制目标错误。</summary>
    public static Streamer ForResponse(Streamer value)
    {
        if (value.Error is not null)
        {
            value.Error = LocalizedText.Format(value.ErrorCode, value.Error, value.ErrorArguments);
        }
        return value;
    }

    /// <summary>为当前请求格式化场次详情中的系统提示。</summary>
    public static SessionDetail ForResponse(SessionDetail value)
    {
        ForResponse(value.Session);
        return value;
    }

    /// <summary>只翻译系统生成的事件文本，保留弹幕和其他原始内容。</summary>
    public static EventPage ForResponse(EventPage value)
    {
        foreach (var item in value.Items)
        {
            if (item.Kind == "Gap")
            {
                item.Text = LocalizedText.Format(item.TextCode, item.Text, item.TextArguments);
            }
        }
        return value;
    }

    /// <summary>为回放消息窗口格式化系统生成的事件文本。</summary>
    public static PlaybackEventWindow ForResponse(PlaybackEventWindow value)
    {
        foreach (var item in value.Items)
        {
            if (item.Kind == "Gap")
            {
                item.Text = LocalizedText.Format(item.TextCode, item.Text, item.TextArguments);
            }
        }
        return value;
    }
}
