using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Infrastructure.Playback;

internal sealed record ContinuousPlaybackPlan(IPlaybackFragmentSource? Source);
