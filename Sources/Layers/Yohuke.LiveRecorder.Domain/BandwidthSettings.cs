namespace Yohuke.LiveRecorder.Domain;

/// <summary>媒体出口的账号下载上限和智能带宽分配设置。</summary>
public sealed class BandwidthSettings
{
    public long UserDownloadLimitBytesPerSecond { get; set; }
    public bool SmartLimitEnabled { get; set; }
    public long TotalBytesPerSecond { get; set; }
    public int DownloadGuaranteedPercent { get; set; } = 10;
}
