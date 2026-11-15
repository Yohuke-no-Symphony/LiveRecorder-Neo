using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>验证清晰度列表、顺序和唯一默认档位的一致性。</summary>
public static class TranscodingSettingsPolicy
{
    /// <summary>拒绝重复或缺失档位，停用档位不能成为默认播放选择。</summary>
    public static void Validate(TranscodingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Profiles is null || settings.QualityOrder is null || settings.Profiles.Count > 12)
        {
            throw new ArgumentException("转码档位列表无效。");
        }
        foreach (var profile in settings.Profiles)
        {
            TranscodeConfigurationPolicy.ValidateProfile(profile);
        }
        var ids = settings.Profiles.Select(x => x.Id).Prepend("original").ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length
            || settings.QualityOrder.Count != ids.Length
            || !settings.QualityOrder.ToHashSet(StringComparer.Ordinal).SetEquals(ids)
            || settings.DefaultPlaybackQualityId != "original"
            && !settings.Profiles.Any(x => x.Id == settings.DefaultPlaybackQualityId && x.Enabled))
        {
            throw new ArgumentException("清晰度顺序必须包含每个档位一次，默认档位必须可用。");
        }
    }
}
