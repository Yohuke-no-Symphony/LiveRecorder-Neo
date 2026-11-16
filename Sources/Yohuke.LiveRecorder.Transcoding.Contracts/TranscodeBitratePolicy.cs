namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>统一转码目标码率与成品平均码率的允许范围。</summary>
public static class TranscodeBitratePolicy
{
    /// <summary>取源媒体与目标档位之间较低的码率作为编码目标。</summary>
    public static long GetTargetBitrate(long sourceBitrate, int profileBitrateKbps) =>
        Math.Min(sourceBitrate, profileBitrateKbps * 1000L);

    /// <summary>保留百分之十五的 VBR 容差与每秒一千比特的测量余量。</summary>
    public static double GetMaximumBitrate(long targetBitrate) => targetBitrate * 115d / 100 + 1000;

    /// <summary>判断正数实测码率是否处于目标允许范围内。</summary>
    public static bool IsWithinLimit(long actualBitrate, long targetBitrate) =>
        targetBitrate > 0 && actualBitrate > 0 && actualBitrate <= GetMaximumBitrate(targetBitrate);
}
