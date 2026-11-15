using System.Security.Cryptography;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Encoding;

internal sealed class TranscodeOutputValidator(MediaInspector inspector)
{
    internal async Task<TranscodeOutputManifest> ValidateAsync(string ffprobePath, string directory,
        TranscodeMediaInfo source, TranscodeMediaInfo expected, string encoder, CancellationToken cancellationToken,
        TranscodeProfile? profile = null)
    {
        profile ??= new();
        var playlist = Path.Combine(directory, "index.m3u8");
        if (new FileInfo(playlist).Length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("The transcoded playlist exceeds the supported size.");
        }
        var text = await File.ReadAllTextAsync(playlist, cancellationToken);
        var names = profile.SingleFile ? ["media.mp4", "index.m3u8"]
            : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.StartsWith('#')).Append("init.mp4").Append("index.m3u8").Distinct(StringComparer.Ordinal).ToArray();
        if (names.Any(name => !TranscodePlaylistPolicy.IsSupportedFileName(name)))
        {
            throw new InvalidDataException("The transcoded playlist contains invalid local file references");
        }
        var lengths = names.ToDictionary(name => name, name => new FileInfo(Path.Combine(directory, name)).Length, StringComparer.Ordinal);
        var parsed = TranscodePlaylistPolicy.Parse(text, profile, lengths, expected.FrameRate);
        var media = await inspector.InspectAsync(ffprobePath, playlist, cancellationToken);
        if (media.Width != expected.Width || media.Height != expected.Height || media.VideoCodec != "h264"
            || media.HasAudio != source.HasAudio || (media.HasAudio && media.AudioCodec != "aac")
            || Math.Abs(media.DurationMs - source.DurationMs) > Math.Max(250, 2000 / expected.FrameRate))
        {
            throw new InvalidDataException("Transcoded output failed dimension, codec, or duration validation");
        }
        var boundaries = new List<double>();
        var elapsed = 0d;
        foreach (var duration in parsed.SegmentDurations)
        {
            boundaries.Add(elapsed);
            elapsed += duration;
        }
        await inspector.ValidatePlaylistTimelineAsync(ffprobePath, playlist, boundaries, media.FrameRate, cancellationToken);
        media.SourceStartMs = source.SourceStartMs;
        var manifest = new TranscodeOutputManifest { Source = source, Media = media, Encoder = encoder };
        foreach (var name in parsed.FileNames)
        {
            var path = Path.Combine(directory, name);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length <= 0)
            {
                throw new InvalidDataException("The transcoded output contains an empty file");
            }
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            manifest.Files.Add(new() { Name = name, Length = stream.Length, Sha256 = Convert.ToHexStringLower(hash) });
        }
        return manifest;
    }
}
