using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;

namespace Yohuke.LiveRecorder.Infrastructure.Playback;

/// <summary>解析默认或手动清晰度，已发布成品不可用时保留原画播放能力。</summary>
public sealed class RenditionPlaybackService(LibraryService library, ISettingsStore settingsStore,
    TranscodingCoordinator transcoding, ContinuousPlaybackService continuous, PlaybackService playback)
{
    /// <summary>以全场时间请求清晰度，并分别返回用户请求与实际播放档位。</summary>
    public async Task<PlaybackResponse?> OpenAsync(string sessionId, double offsetMs, string? qualityId, bool prefetch,
        CancellationToken cancellationToken = default)
    {
        var detail = await library.GetAsync(sessionId, cancellationToken);
        if (!double.IsFinite(offsetMs) || offsetMs < 0 || offsetMs > detail.DurationMs + 100
            || qualityId?.Length > 64)
        {
            throw new InfrastructureException(MessageCode.PLAYBACK_TIME_OUT_OF_RANGE);
        }
        var settings = (await settingsStore.GetAsync(cancellationToken)).Transcoding;
        var requested = string.IsNullOrEmpty(qualityId) ? settings.DefaultPlaybackQualityId : qualityId;
        var inGap = detail.Gaps.Any(gap => offsetMs >= gap.StartMs && offsetMs < gap.EndMs);
        var segment = inGap ? null : detail.Segments.FirstOrDefault(item => offsetMs >= item.StartOffsetMs - 100
            && offsetMs < item.StartOffsetMs + item.DurationMs);
        var renditions = segment is { IsClosed: true }
            ? await transcoding.GetRenditionsAsync(sessionId, segment.Id, cancellationToken)
            : Array.Empty<PlaybackRendition>();
        var profiles = settings.Profiles.Where(profile => profile.Enabled).ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        var ready = renditions.GroupBy(item => item.ProfileId).ToDictionary(group => group.Key,
            group => group.OrderByDescending(item => profiles.TryGetValue(item.ProfileId, out var profile)
                    && item.RecipeHash == TranscodeRecipe.Hash(profile))
                .ThenByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Attempt).First(), StringComparer.Ordinal);
        var qualities = new List<PlaybackQuality>();
        foreach (var id in settings.QualityOrder)
        {
            if (id == "original")
            {
                qualities.Add(new("original", "原画", true, null, "Ready"));
            }
            else if (profiles.TryGetValue(id, out var profile))
            {
                var available = ready.TryGetValue(id, out var rendition);
                qualities.Add(new(id, profile.Name, available,
                    available ? Math.Min(rendition!.Width, rendition.Height) : profile.Height, available ? "Ready" : "Unavailable"));
            }
        }
        if (segment is not null && profiles.ContainsKey(requested) && ready.TryGetValue(requested, out var selected))
        {
            return new()
            {
                Url = $"/api/sessions/{Uri.EscapeDataString(sessionId)}/renditions/{Uri.EscapeDataString(segment.Id)}/{Uri.EscapeDataString(selected.Id)}/index.m3u8",
                StartOffsetMs = segment.StartOffsetMs + (selected.PlaybackTimestampOffsetMs ?? selected.SourceStartMs),
                MediaStartOffsetMs = segment.StartOffsetMs,
                TimestampMapping = selected.PlaybackTimestampOffsetMs is not null ? "HlsInitPts" : null,
                MediaEndTimestampMs = selected.MediaEndTimestampMs,
                EndOffsetMs = segment.StartOffsetMs + segment.DurationMs,
                RequestedOffsetMs = offsetMs,
                DurationMs = detail.DurationMs,
                RequestedQualityId = requested,
                EffectiveQualityId = requested,
                ArtifactVersion = selected.Id,
                Qualities = qualities
            };
        }
        var window = await continuous.TryOpenAsync(sessionId, offsetMs, cancellationToken);
        window ??= prefetch
            ? await playback.TryPrefetchAsync(sessionId, offsetMs, cancellationToken)
            : await playback.OpenAsync(sessionId, offsetMs, cancellationToken);
        if (window is not null)
        {
            window.RequestedQualityId = requested;
            window.EffectiveQualityId = "original";
            window.FallbackReason = requested == "original" ? null : "Unavailable";
            window.Qualities = qualities;
        }
        return window;
    }
}
