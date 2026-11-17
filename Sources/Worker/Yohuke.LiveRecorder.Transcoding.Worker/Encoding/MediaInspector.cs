using System.Globalization;
using System.Text.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Encoding;

internal sealed class MediaInspector(WorkerProcessRunner processes)
{
    private const int SOURCE_MEDIA_CACHE_VERSION = 1;
    private readonly SemaphoreSlim[] sourceGates = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    internal async Task ValidatePlaylistTimelineAsync(string tool, string path, IReadOnlyList<double> boundaries,
        double frameRate, CancellationToken cancellationToken)
    {
        var timeline = new HlsPacketTimeline(boundaries, frameRate);
        var result = await processes.RunAsync(tool,
            ["-v", "error", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,dts_time,flags", "-of", "compact=p=0:nk=0", path],
            cancellationToken, outputLine: timeline.Add);
        if (result.ExitCode != 0)
        {
            throw new InvalidDataException("Transcoded timeline validation failed: " + result.Diagnostic);
        }
        timeline.Complete();
    }

    internal async Task<TranscodeMediaInfo> InspectInputAsync(string tool, string path, CancellationToken cancellationToken,
        Action<string>? log = null)
    {
        var gate = sourceGates[(StringComparer.Ordinal.GetHashCode(path) & int.MaxValue) % sourceGates.Length];
        await gate.WaitAsync(cancellationToken);
        try
        {
            var metadata = path + ".media.json";
            if (File.Exists(metadata))
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<SourceMediaCache>(await File.ReadAllTextAsync(metadata, cancellationToken), WorkerProtocol.JsonOptions);
                    if (cached is { Version: SOURCE_MEDIA_CACHE_VERSION, Media: { Width: > 0, Height: > 0, DurationMs: > 0, FrameRate: > 0, VideoBitrate: > 0 } cachedMedia }
                        && double.IsFinite(cachedMedia.DurationMs) && double.IsFinite(cachedMedia.FrameRate)
                        && (!cachedMedia.HasAudio || cachedMedia.AudioBitrate > 0))
                    {
                        log?.Invoke("Reusing cached source media information.");
                        return cachedMedia;
                    }
                }
                catch (JsonException)
                {
                }
            }
            var media = await InspectAsync(tool, path, cancellationToken, log);
            var temporary = metadata + ".tmp";
            try
            {
                var cache = new SourceMediaCache { Version = SOURCE_MEDIA_CACHE_VERSION, Media = media };
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(cache, WorkerProtocol.JsonOptions), cancellationToken);
                File.Move(temporary, metadata, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
            return media;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<TranscodeMediaInfo> InspectAsync(string tool, string path, CancellationToken cancellationToken,
        Action<string>? log = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var result = await processes.RunAsync(tool,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], timeout.Token,
            errorLine: message => log?.Invoke("ffprobe: " + message));
        if (result.ExitCode != 0)
        {
            throw new InvalidDataException("Media probing failed: " + result.Diagnostic);
        }
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(stream => Text(stream, "codec_type") == "video");
        var audio = streams.FirstOrDefault(stream => Text(stream, "codec_type") == "audio");
        var format = root.GetProperty("format");
        var rate = FrameRate(Text(video, "avg_frame_rate"));
        if (rate <= 0)
        {
            rate = FrameRate(Text(video, "r_frame_rate"));
        }
        var width = (int)Number(video, "width");
        var height = (int)Number(video, "height");
        var sampleAspect = Text(video, "sample_aspect_ratio").Replace(':', '/');
        var sar = FrameRate(sampleAspect);
        if (sar > 0)
        {
            width = Math.Max(2, (int)Math.Round(width * sar));
        }
        if (video.TryGetProperty("side_data_list", out var sideData)
            && sideData.EnumerateArray().Any(item => Math.Abs(Number(item, "rotation")) % 180 == 90))
        {
            (width, height) = (height, width);
        }
        var resultInfo = new TranscodeMediaInfo
        {
            Width = width,
            Height = height,
            FrameRate = rate,
            DurationMs = Number(format, "duration") * 1000,
            SourceStartMs = Number(video, "start_time") * 1000,
            HasAudio = audio.ValueKind != JsonValueKind.Undefined,
            VideoCodec = Text(video, "codec_name"),
            AudioCodec = Text(audio, "codec_name"),
            IsHdr = Text(video, "color_transfer") is "smpte2084" or "arib-std-b67"
        };
        if (resultInfo.DurationMs <= 0 || width < 2 || height < 2 || !double.IsFinite(rate) || rate <= 0)
        {
            throw new InvalidDataException("The media has invalid video dimensions, duration, or frame rate");
        }
        log?.Invoke("Measuring video and audio bitrates from media packets.");
        var videoStatistics = new TranscodePacketStatistics();
        var audioStatistics = new TranscodePacketStatistics();
        var statistics = new Dictionary<int, TranscodePacketStatistics> { [(int)Number(video, "index")] = videoStatistics };
        if (resultInfo.HasAudio)
        {
            statistics[(int)Number(audio, "index")] = audioStatistics;
        }
        var packetResult = await processes.RunAsync(tool,
            ["-v", "error", "-show_packets", "-show_entries", "packet=stream_index,size,pts_time,duration_time", "-of", "compact=p=0:nk=0", path],
            cancellationToken, outputLine: line => TranscodePacketStatistics.Parse(line, statistics),
            errorLine: message => log?.Invoke("ffprobe: " + message));
        if (packetResult.ExitCode != 0)
        {
            throw new InvalidDataException("Media packet probing failed: " + packetResult.Diagnostic);
        }
        resultInfo.VideoBitrate = videoStatistics.Bitrate;
        resultInfo.AudioBitrate = audioStatistics.Bitrate;
        return resultInfo;
    }

    private static double FrameRate(string value)
    {
        var parts = value.Split('/');
        return parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator) && denominator > 0
            ? numerator / denominator : 0;
    }

    private static string Text(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value) ? value.ToString() : "";

    private static double Number(JsonElement element, string property) => double.TryParse(Text(element, property),
        CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
}
