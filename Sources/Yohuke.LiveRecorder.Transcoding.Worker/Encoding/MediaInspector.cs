using System.Globalization;
using System.Text.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Encoding;

internal sealed class MediaInspector(WorkerProcessRunner processes)
{
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
            throw new InvalidDataException("转码时间轴验证失败：" + result.Diagnostic);
        }
        timeline.Complete();
    }

    internal async Task<TranscodeMediaInfo> InspectInputAsync(string tool, string path, CancellationToken cancellationToken)
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
                    var cached = JsonSerializer.Deserialize<TranscodeMediaInfo>(await File.ReadAllTextAsync(metadata, cancellationToken), WorkerProtocol.JsonOptions);
                    if (cached is { Width: > 0, Height: > 0, DurationMs: > 0, FrameRate: > 0 })
                    {
                        return cached;
                    }
                }
                catch (JsonException)
                {
                }
            }
            var media = await InspectAsync(tool, path, cancellationToken);
            var temporary = metadata + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(media, WorkerProtocol.JsonOptions), cancellationToken);
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

    internal async Task<TranscodeMediaInfo> InspectAsync(string tool, string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var result = await processes.RunAsync(tool,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], timeout.Token);
        if (result.ExitCode != 0)
        {
            throw new InvalidDataException("媒体探测失败：" + result.Diagnostic);
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
            VideoBitrate = (long)Number(video, "bit_rate"),
            AudioBitrate = (long)Number(audio, "bit_rate"),
            IsHdr = Text(video, "color_transfer") is "smpte2084" or "arib-std-b67"
        };
        if (resultInfo.DurationMs <= 0 || width < 2 || height < 2 || !double.IsFinite(rate) || rate <= 0)
        {
            throw new InvalidDataException("媒体没有有效的视频尺寸、时长或帧率");
        }
        if (resultInfo.VideoBitrate <= 0 || (resultInfo.HasAudio && resultInfo.AudioBitrate <= 0))
        {
            var bitrates = new Dictionary<int, PacketBitrate> { [(int)Number(video, "index")] = new() };
            if (resultInfo.HasAudio)
            {
                bitrates[(int)Number(audio, "index")] = new();
            }
            var packetResult = await processes.RunAsync(tool,
                ["-v", "error", "-show_packets", "-show_entries", "packet=stream_index,size,pts_time,duration_time", "-of", "compact=p=0:nk=0", path],
                cancellationToken, outputLine: line => PacketBitrate.Parse(line, bitrates));
            if (packetResult.ExitCode == 0)
            {
                if (resultInfo.VideoBitrate <= 0)
                {
                    resultInfo.VideoBitrate = bitrates[(int)Number(video, "index")].Calculate(resultInfo.DurationMs);
                }
                if (resultInfo.HasAudio && resultInfo.AudioBitrate <= 0)
                {
                    resultInfo.AudioBitrate = bitrates[(int)Number(audio, "index")].Calculate(resultInfo.DurationMs);
                }
            }
        }
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
