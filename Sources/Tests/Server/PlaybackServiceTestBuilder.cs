using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Tests;

internal sealed class PlaybackServiceTestBuilder : IPlaybackWindowBuilder
{
    internal List<PlaybackWindowRequest> Requests { get; } = [];
    internal bool SkipPrefetch { get; set; }
    internal Func<PlaybackWindowRequest, CancellationToken, Task<PlaybackWindowResult>>? Build { get; set; }

    public async Task<PlaybackWindowResult> BuildAsync(PlaybackWindowRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Build is null ? await PublishAsync(request, cancellationToken) : await Build(request, cancellationToken);
    }

    public async Task<PlaybackWindowResult?> TryPrefetchAsync(PlaybackWindowRequest request, CancellationToken cancellationToken)
    {
        return SkipPrefetch ? null : await BuildAsync(request, cancellationToken);
    }

    internal static async Task<PlaybackWindowResult> PublishAsync(PlaybackWindowRequest request, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.OutputDirectory);
        var playlist = Path.Combine(request.OutputDirectory, "index.m3u8");
        await File.WriteAllTextAsync(playlist, "#EXTM3U\n#EXT-X-ENDLIST\n", cancellationToken);
        return new()
        {
            PlaylistPath = playlist,
            SourceStartMs = request.SourceStartMs,
            SourceEndMs = request.SourceStartMs + request.DurationMs,
            DurationMs = request.DurationMs
        };
    }
}
