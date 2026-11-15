using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Globalization;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Encoding;

internal sealed class TranscodeOutputValidator(MediaInspector inspector)
{
    private static Regex InitializationUri { get; } = new("#EXT-X-MAP:URI=\"([^\"]+)\"", RegexOptions.Compiled);

    private static Regex SafeFileName { get; } = new("^(init\\.mp4|chunk_[0-9]+\\.m4s)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal async Task<TranscodeOutputManifest> ValidateAsync(string ffprobePath, string directory,
        TranscodeMediaInfo source, TranscodeMediaInfo expected, string encoder, CancellationToken cancellationToken)
    {
        var playlist = Path.Combine(directory, "index.m3u8");
        var text = await File.ReadAllTextAsync(playlist, cancellationToken);
        if (!text.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal) || !text.Contains("#EXT-X-PLAYLIST-TYPE:VOD", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The transcoded playlist is incomplete");
        }
        var names = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#')).ToList();
        names.AddRange(InitializationUri.Matches(text).Select(match => match.Groups[1].Value));
        if (names.Count < 2 || names.Any(name => !SafeFileName.IsMatch(name)))
        {
            throw new InvalidDataException("The transcoded playlist contains invalid local file references");
        }
        var media = await inspector.InspectAsync(ffprobePath, playlist, cancellationToken);
        if (media.Width != expected.Width || media.Height != expected.Height || media.VideoCodec != "h264"
            || media.HasAudio != source.HasAudio || (media.HasAudio && media.AudioCodec != "aac")
            || Math.Abs(media.DurationMs - source.DurationMs) > Math.Max(250, 2000 / expected.FrameRate))
        {
            throw new InvalidDataException("Transcoded output failed dimension, codec, or duration validation");
        }
        var boundaries = new List<double>();
        var elapsed = 0d;
        foreach (var line in text.Split('\n').Where(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal)))
        {
            boundaries.Add(elapsed);
            var duration = double.Parse(line.AsSpan(8).Trim().TrimEnd(','), CultureInfo.InvariantCulture);
            if (!double.IsFinite(duration) || duration <= 0 || duration > 6.5)
            {
                throw new InvalidDataException("A transcoded segment duration is outside the allowed range");
            }
            elapsed += duration;
        }
        await inspector.ValidatePlaylistTimelineAsync(ffprobePath, playlist, boundaries, media.FrameRate, cancellationToken);
        media.SourceStartMs = source.SourceStartMs;
        var manifest = new TranscodeOutputManifest { Source = source, Media = media, Encoder = encoder };
        foreach (var name in names.Append("index.m3u8").Distinct(StringComparer.Ordinal))
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
