namespace Yohuke.LiveRecorder.Infrastructure.Playback;

internal sealed record ContinuousPlaybackFragmentMetadata(string SourceKey, int Sequence,
    string? InitializationContentHash = null, string? MediaContentHash = null);
