using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Server.Bandwidth;

internal static class BandwidthSettingsPolicy
{
    internal const long MAX_RATE = 9_007_199_254_740_991;

    internal static void Validate(BandwidthSettings? settings)
    {
        if (settings is null || settings.UserDownloadLimitBytesPerSecond is < 0 or > MAX_RATE ||
            settings.TotalBytesPerSecond is < 0 or > MAX_RATE || settings.DownloadGuaranteedPercent is < 0 or > 100 ||
            (settings.SmartLimitEnabled && settings.TotalBytesPerSecond == 0))
        {
            throw new InfrastructureException(MessageCode.SETTINGS_INVALID);
        }
    }
}
