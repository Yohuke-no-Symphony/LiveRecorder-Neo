using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Server.Bandwidth;

internal sealed record BandwidthConfiguration(long TotalRate, long UserDownloadRate, int DownloadPercent)
{
    internal static BandwidthConfiguration From(BandwidthSettings settings)
    {
        BandwidthSettingsPolicy.Validate(settings);
        return new(settings.SmartLimitEnabled ? settings.TotalBytesPerSecond : 0,
            settings.UserDownloadLimitBytesPerSecond, settings.DownloadGuaranteedPercent);
    }
}
