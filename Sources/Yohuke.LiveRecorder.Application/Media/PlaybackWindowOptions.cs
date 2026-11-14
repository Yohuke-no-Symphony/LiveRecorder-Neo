namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>Defines the shared target duration used to select and generate playback windows.</summary>
public sealed class PlaybackWindowOptions
{
    public const int DEFAULT_DURATION_MS = 30000;

    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(DEFAULT_DURATION_MS);
}
