using System.Text.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>将编码参数转为与显示顺序和默认档位无关的稳定身份。</summary>
public static class TranscodeRecipe
{
    /// <summary>生成 H.264/AAC、六秒独立分片的配方版本。</summary>
    public static string Hash(TranscodeProfile profile) => TranscodeAssetRegistry.Hash(JsonSerializer.Serialize(new
    {
        Version = "h264-aac-fmp4-v2",
        profile.Height,
        profile.VideoBitrateKbps,
        profile.MaxFrameRate,
        profile.AudioBitrateKbps
    }));
}
