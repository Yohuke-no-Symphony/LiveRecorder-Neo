using System.Globalization;
using Microsoft.Extensions.Options;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media;

/// <summary>Remuxes a committed keyframe range without reading earlier video bytes.</summary>
public sealed class IndexedPlaybackWindowBuilder : IPlaybackWindowBuilder
{
    private readonly MediaProcessRunner processes;
    private readonly MediaToolOptions options;
    private readonly PlaybackWindowOptions windowOptions;

    /// <summary>Creates the window builder with its shared media process limiter.</summary>
    public IndexedPlaybackWindowBuilder(MediaProcessRunner processes, IOptions<MediaToolOptions> options, IOptions<PlaybackWindowOptions> windowOptions)
    {
        this.processes = processes;
        this.options = options.Value;
        this.windowOptions = windowOptions.Value;
    }

    /// <inheritdoc />
    public Task<PlaybackWindowResult> BuildAsync(PlaybackWindowRequest request, CancellationToken cancellationToken) =>
        BuildCoreAsync(request, null, cancellationToken);

    /// <inheritdoc />
    public async Task<PlaybackWindowResult?> TryPrefetchAsync(PlaybackWindowRequest request, CancellationToken cancellationToken)
    {
        using var reservation = await processes.TryAcquirePlaybackPrefetchAsync(cancellationToken);
        return reservation is null ? null : await BuildCoreAsync(request, reservation, cancellationToken);
    }

    private async Task<PlaybackWindowResult> BuildCoreAsync(PlaybackWindowRequest request, MediaProcessReservation? reservation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        var desiredEnd = Math.Min(request.SourceStartMs + Math.Min(request.DurationMs, windowOptions.Duration.TotalMilliseconds), index.DurationMs);
        var after = index.Keyframes.FirstOrDefault(frame => frame.TimestampMs >= desiredEnd && frame.Offset > first.Offset);
        var endOffset = after?.Offset ?? committed;
        var sourceEndMs = after?.TimestampMs ?? index.DurationMs;
        var videoHeader = index.Entries.LastOrDefault(entry => entry.Kind == "VideoHeader" && entry.Offset < first.Offset)?.HeaderData
            ?? throw new InvalidDataException("No indexed AVC/HEVC decoder configuration is available for this window.");
        var range = new IndexedPlaybackRange
        {
            First = first,
            EndOffset = endOffset,
            EndMs = sourceEndMs,
            TimestampOriginMs = first.TimestampMs,
            ReachesCommittedEnd = endOffset == committed,
            VideoHeader = videoHeader,
            AudioHeader = index.Entries.LastOrDefault(entry => entry.Kind == "AudioHeader" && entry.Offset < first.Offset)?.HeaderData
        };
        return await BuildRangeAsync(request.SourcePath, request.OutputDirectory, request.Transcode, range, false, reservation, cancellationToken);
    }

    internal async Task<PlaybackWindowResult> BuildRangeAsync(string sourcePath, string outputDirectory, bool transcode,
        IndexedPlaybackRange range, bool singleFragment, MediaProcessReservation? reservation, CancellationToken cancellationToken, Action? validateSource = null)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
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
            if (singleFragment)
            {
                arguments.AddRange(["-copyts", "-hls_segment_options", "movflags=+frag_discont:use_editlist=0:avoid_negative_ts=disabled"]);
            }
            if (transcode)
            {
                arguments.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k"]);
            }
            else
            {
                arguments.AddRange(["-c", "copy"]);
            }
            var segmentDuration = singleFragment
                ? (range.EndMs - range.First.TimestampMs) / 1000 + options.HlsSegmentDuration.TotalSeconds
                : options.HlsSegmentDuration.TotalSeconds;
            arguments.AddRange([
                "-avoid_negative_ts", singleFragment ? "disabled" : "make_zero",
                "-f", "hls", "-hls_time", segmentDuration.ToString(CultureInfo.InvariantCulture),
                "-hls_segment_type", "fmp4", "-hls_playlist_type", "vod", "-hls_flags", "independent_segments+temp_file",
                "-hls_fmp4_init_filename", "init.mp4", "-hls_segment_filename", Path.Combine(temporary, "chunk_%05d.m4s"), playlist
            ]);
            async Task Input(Stream output, CancellationToken token)
            {
                validateSource?.Invoke();
                await IndexedFlvRange.CopyAsync(sourcePath, range.First, range.EndOffset, range.TimestampOriginMs,
                    range.VideoHeader, range.AudioHeader, output, token);
            }
            if (reservation is null)
            {
                await processes.RunAsync(MediaProcessKind.Playback, arguments, cancellationToken, Input);
            }
            else
            {
                await reservation.RunAsync(arguments, cancellationToken, Input);
            }
            var lines = await File.ReadAllLinesAsync(playlist, cancellationToken);
            var duration = lines.Where(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                .Sum(line => double.Parse(line.AsSpan(8).TrimEnd(','), CultureInfo.InvariantCulture)) * 1000;
            var fragments = Directory.EnumerateFiles(temporary, "*.m4s").ToArray();
            if (duration <= 0 || fragments.Length == 0 || (singleFragment && fragments.Length != 1))
            {
                throw new InvalidDataException("The playback process did not produce the expected playable fragments.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            validateSource?.Invoke();
            PlaybackWindowPublisher.Publish(temporary, directory);
            return new()
            {
                PlaylistPath = Path.Combine(directory, "index.m3u8"),
                SourceStartMs = range.First.TimestampMs,
                SourceEndMs = range.EndMs,
                ReachesCommittedEnd = range.ReachesCommittedEnd,
                DurationMs = duration
            };
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }
}
