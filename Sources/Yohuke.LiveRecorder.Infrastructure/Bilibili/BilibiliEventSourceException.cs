using Yohuke.DanmuCore.Apis;

namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

/// <summary>标记可通过更换采集身份重试的弹幕来源故障，保留原始失败语义。</summary>
public sealed class BilibiliEventSourceException : Exception
{
    /// <summary>封装平台连接或补录鉴权失败，不用于本地归档、队列和排空故障。</summary>
    public BilibiliEventSourceException(Exception innerException)
        : base("B站弹幕来源请求失败。", innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);
    }

    /// <summary>平台是否明确拒绝当前账号登录态。</summary>
    public bool IsAuthenticationFailure => InnerException is BilibiliAuthenticationException;
}
