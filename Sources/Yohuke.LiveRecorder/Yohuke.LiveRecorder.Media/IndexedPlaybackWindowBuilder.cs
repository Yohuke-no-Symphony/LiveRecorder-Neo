using System.Globalization;
using Microsoft.Extensions.Options;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media;

/// <summary>Remuxes a committed keyframe range without reading earlier video bytes.</summary>
public sealed class IndexedPlaybackWindowBuilder : IPlaybackWindowBuilder
{
    private readonly MediaProcessRunner processes;
    private readonly MediaToolOptions options;

    /// <summary>Creates the window builder with its shared media process limiter.</summary>
    public IndexedPlaybackWindowBuilder(MediaProcessRunner processes, IOptions<MediaToolOptions> options)
    {
        this.processes = processes;
        this.options = options.Value;
    }

    /// <inheritdoc />
    public async Task<PlaybackWindowResult> BuildAsync(PlaybackWindowRequest request, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(request.SourceStartMs) || request.SourceStartMs < 0
            || !double.IsFinite(request.DurationMs) || request.DurationMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "A playback window needs a finite, positive range.");
        }
        var sourceLength = new FileInfo(request.SourcePath).Length;
        var committed = request.CommittedBytes > 0 ? Math.Min(request.CommittedBytes, sourceLength) : request.IsClosed ? sourceLength : 0;
        var index = await FlvIndexSnapshot.ReadAsync(request.IndexPath, committed, cancellationToken);
        var first = index.Keyframes.Where(frame => frame.TimestampMs <= request.SourceStartMs).LastOrDefault()
            ?? index.Keyframes.FirstOrDefault();
        if (first is null || request.SourceStartMs >= index.DurationMs)
        {
            throw new InvalidOperationException("The requested playback position has not been committed yet.");
        }
        var desiredEnd = Math.Min(request.SourceStartMs + Math.Min(request.DurationMs, options.PlaybackWindowDuration.TotalMilliseconds), index.DurationMs);
        var after = index.Keyframes.FirstOrDefault(frame => frame.TimestampMs >= desiredEnd && frame.Offset > first.Offset);
        var endOffset = after?.Offset ?? committed;
        var expectedDuration = (after?.TimestampMs ?? index.DurationMs) - first.TimestampMs;
        var directory = Path.GetFullPath(request.OutputDirectory);
        var temporary = directory + ".building-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temporary);
        try
        {
            var playlist = Path.Combine(temporary, "index.m3u8");
            var arguments = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-fflags", "+genpts", "-f", "flv", "-i", "pipe:0",
                "-map", "0:v:0", "-map", "0:a:0?"
            };
            if (request.Transcode)
            {
                arguments.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k"]);
            }
            else
            {
                arguments.AddRange(["-c", "copy"]);
            }
            arguments.AddRange([
                "-avoid_negative_ts", "make_zero",
                "-f", "hls", "-hls_time", options.HlsSegmentDuration.TotalSeconds.ToString(CultureInfo.InvariantCulture),
                "-hls_segment_type", "fmp4", "-hls_playlist_type", "vod", "-hls_flags", "independent_segments+temp_file",
                "-hls_fmp4_init_filename", "init.mp4", "-hls_segment_filename", Path.Combine(temporary, "chunk_%05d.m4s"), playlist
            ]);
            await processes.RunAsync(MediaProcessKind.Playback, arguments, cancellationToken, (output, token) => IndexedFlvRange.CopyAsync(request.SourcePath, index, first, endOffset, output, token));
            var lines = await File.ReadAllLinesAsync(playlist, cancellationToken);
            var duration = lines.Where(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                .Sum(line => double.Parse(line.AsSpan(8).TrimEnd(','), CultureInfo.InvariantCulture)) * 1000;
            if (duration <= 0 || !Directory.EnumerateFiles(temporary, "*.m4s").Any())
            {
                throw new InvalidDataException("The playback process did not produce any playable fragments.");
            }
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(temporary))
            {
                File.Move(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: false);
            }
            return new()
            {
                PlaylistPath = Path.Combine(directory, "index.m3u8"),
                SourceStartMs = first.TimestampMs,
                DurationMs = Math.Min(duration, expectedDuration + 150)
            };
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }
}
