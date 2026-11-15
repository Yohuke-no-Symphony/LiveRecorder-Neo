using System.Globalization;
using System.Text.Json;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Media;

/// <summary>以流式包统计复验媒体，内存占用不随录播长度增长。</summary>
public sealed class TranscodeMediaInspector(MediaProcessRunner processes) : ITranscodeMediaInspector
{
    /// <inheritdoc />
    public async Task<TranscodeMediaInfo> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        var metadata = await processes.RunAsync(MediaProcessKind.Probe,
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", Path.GetFullPath(path)], cancellationToken);
        using var document = JsonDocument.Parse(metadata.StandardOutput);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(stream => Text(stream, "codec_type") == "video");
        var audio = streams.FirstOrDefault(stream => Text(stream, "codec_type") == "audio");
        if (video.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException("Media contains no video stream.");
        }
        var videoIndex = (int)Number(video, "index");
        var audioIndex = audio.ValueKind == JsonValueKind.Undefined ? -1 : (int)Number(audio, "index");
        var videoStatistics = new TranscodePacketStatistics();
        var audioStatistics = new TranscodePacketStatistics();
        void ReadPacket(string line)
        {
            var index = -1;
            long size = 0;
            var pts = double.NaN;
            var duration = 0d;
            foreach (var field in line.Split('|'))
            {
                var separator = field.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }
                var name = field.AsSpan(0, separator);
                var value = field.AsSpan(separator + 1);
                if (name.SequenceEqual("stream_index"))
                {
                    int.TryParse(value, CultureInfo.InvariantCulture, out index);
                }
                else if (name.SequenceEqual("size"))
                {
                    long.TryParse(value, CultureInfo.InvariantCulture, out size);
                }
                else if (name.SequenceEqual("pts_time"))
                {
                    if (!double.TryParse(value, CultureInfo.InvariantCulture, out pts))
                    {
                        pts = double.NaN;
                    }
                }
                else if (name.SequenceEqual("duration_time"))
                {
                    double.TryParse(value, CultureInfo.InvariantCulture, out duration);
                }
            }
            var statistics = index == videoIndex ? videoStatistics : index == audioIndex ? audioStatistics : null;
            if (statistics is null || size <= 0 || !double.IsFinite(pts) || !double.IsFinite(duration))
            {
                return;
            }
            statistics.Bytes += size;
            statistics.FirstPts = Math.Min(statistics.FirstPts, pts);
            statistics.LastPts = Math.Max(statistics.LastPts, pts + Math.Max(0, duration));
        }
        await processes.RunAsync(MediaProcessKind.Probe,
            ["-v", "error", "-show_packets", "-show_entries", "packet=stream_index,size,pts_time,duration_time", "-of", "compact=p=0:nk=0", Path.GetFullPath(path)],
            cancellationToken, outputLine: ReadPacket);
        if (videoStatistics.Duration <= 0 || videoStatistics.Bytes == 0)
        {
            throw new InvalidDataException("Media packet timing is unavailable.");
        }
        var width = (int)Number(video, "width");
        var height = (int)Number(video, "height");
        var aspect = Ratio(Text(video, "sample_aspect_ratio"));
        if (aspect > 0)
        {
            width = (int)Math.Round(width * aspect);
        }
        if (video.TryGetProperty("side_data_list", out var sideData)
            && sideData.EnumerateArray().Any(side => Math.Abs(Number(side, "rotation")) % 180 is > 45 and < 135))
        {
            (width, height) = (height, width);
        }
        var frameRate = Ratio(Text(video, "avg_frame_rate"));
        if (frameRate <= 0)
        {
            frameRate = Ratio(Text(video, "r_frame_rate"));
        }
        var media = new TranscodeMediaInfo
        {
            DurationMs = Math.Max(videoStatistics.LastPts, audioIndex >= 0 ? audioStatistics.LastPts : videoStatistics.LastPts)
                * 1000 - videoStatistics.FirstPts * 1000,
            Width = width,
            Height = height,
            FrameRate = frameRate,
            VideoBitrate = videoStatistics.Bitrate,
            AudioBitrate = audioStatistics.Bitrate,
            HasAudio = audioIndex >= 0,
            VideoCodec = Text(video, "codec_name"),
            AudioCodec = Text(audio, "codec_name"),
            IsHdr = Text(video, "color_transfer") is "smpte2084" or "arib-std-b67",
            SourceStartMs = videoStatistics.FirstPts * 1000
        };
        if (string.Equals(Path.GetExtension(path), ".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            var timing = await FragmentedMp4Timeline.ReadAsync(Path.GetDirectoryName(Path.GetFullPath(path))!, cancellationToken);
            media.RawVideoStartMs = timing.PresentationMs;
            media.FirstVideoSampleDurationMs = timing.DurationMs;
            media.RawVideoEndMs = videoStatistics.LastPts * 1000;
        }
        return media;
    }

    private static string Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.ToString() : "";

    private static double Number(JsonElement value, string name) =>
        double.TryParse(Text(value, name), CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : 0;

    private static double Ratio(string value)
    {
        var pieces = value.Replace(':', '/').Split('/');
        return pieces.Length == 2 && double.TryParse(pieces[0], CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(pieces[1], CultureInfo.InvariantCulture, out var denominator) && denominator > 0
            && double.IsFinite(numerator) && double.IsFinite(denominator) ? numerator / denominator : 0;
    }
}
