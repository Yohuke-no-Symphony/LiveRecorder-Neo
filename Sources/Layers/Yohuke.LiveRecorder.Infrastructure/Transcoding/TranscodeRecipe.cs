using System.Text.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>将编码参数转为与显示顺序和默认档位无关的稳定身份。</summary>
public static class TranscodeRecipe
{
    /// <summary>保留默认配方的历史身份，并将自定义分片参数纳入新配方版本。</summary>
    public static string Hash(TranscodeProfile profile)
    {
        if (TranscodeConfigurationPolicy.IsDefaultPackaging(profile))
        {
            return TranscodeAssetRegistry.Hash(JsonSerializer.Serialize(new
            {
                Version = "h264-aac-fmp4-v2",
                profile.Height,
                profile.VideoBitrateKbps,
                profile.MaxFrameRate,
                profile.AudioBitrateKbps
            }));
        }
        return TranscodeAssetRegistry.Hash(JsonSerializer.Serialize(new
        {
            Version = "h264-aac-fmp4-v3",
            profile.Height,
            profile.VideoBitrateKbps,
            profile.MaxFrameRate,
            profile.AudioBitrateKbps,
            profile.SegmentDurationSeconds,
            profile.KeyframeIntervalSeconds,
            profile.SingleFile
        }));
    }
}
