using System.Globalization;
using System.Text;
using System.Text.Json;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media;

/// <summary>Probes media and creates validated, independent repair and export derivatives.</summary>
public sealed class FfmpegMediaProcessor : IMediaProcessor
{
    private readonly MediaProcessRunner processes;

    /// <summary>Creates the processor using the shared media process limiter.</summary>
    public FfmpegMediaProcessor(MediaProcessRunner processes)
    {
        this.processes = processes;
    }

    /// <inheritdoc />
    public async Task<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        return (await ProbeDetailsAsync(path, cancellationToken)).Probe;
    }

    /// <inheritdoc />
    public async Task<MediaRepairResult> RepairAsync(string inputPath, string outputPath, IProgress<double> progress, CancellationToken cancellationToken)
    {
        EnsureDifferentOutput(inputPath, outputPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var committed = new List<string>();
        try
        {
            await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.Asynchronous);
            var updates = new CallbackProgress<MediaRecordingUpdate>(_ => progress.Report(Math.Clamp((double)input.Position / Math.Max(1, input.Length), 0, 0.95)));
            var outputs = await FlvArchiveEngine.RunAsync(input, new()
            {
                OutputDirectory = temporary,
                Title = Path.GetFileNameWithoutExtension(outputPath),
                StartedAtUtc = DateTime.UnixEpoch,
                SessionStartedAtUtc = DateTime.UnixEpoch
            }, updates, cancellationToken);
            if (outputs.Count == 0)
            {
                throw new InvalidDataException("The input contains no repairable media.");
            }
            var result = new MediaRepairResult();
            for (var index = 0; index < outputs.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = outputs[index];
                var probe = await ProbeAsync(source.Path, cancellationToken);
                if (probe.DurationMs <= 0 || probe.VideoCodec.Length == 0)
                {
                    throw new InvalidDataException("A repaired encoding epoch did not contain valid video.");
                }
                var destination = index == 0
                    ? Path.GetFullPath(outputPath)
                    : Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(outputPath)}_part{index + 1:D4}.flv");
                var destinationIndex = Path.ChangeExtension(destination, ".index.jsonl");
                File.Move(source.Path, destination, overwrite: false);
                committed.Add(destination);
                File.Move(source.IndexPath, destinationIndex, overwrite: false);
                committed.Add(destinationIndex);
                var diagnostic = Path.ChangeExtension(source.Path, ".issues.txt");
                if (File.Exists(diagnostic))
                {
                    var destinationDiagnostic = Path.ChangeExtension(destination, ".issues.txt");
                    File.Move(diagnostic, destinationDiagnostic, overwrite: false);
                    committed.Add(destinationDiagnostic);
                }
                result.Outputs.Add(new()
                {
                    Path = destination,
                    IndexPath = destinationIndex,
                    SourceStartMs = source.StartOffsetMs,
                    DurationMs = source.DurationMs
                });
            }
            progress.Report(1);
            return result;
        }
        catch
        {
            foreach (var path in committed)
            {
                File.Delete(path);
            }
            throw;
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    /// <inheritdoc />
    public async Task<MediaExportResult> ExportAsync(MediaExportRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(request.StartMs) || request.StartMs < 0 || !double.IsFinite(request.DurationMs) || request.DurationMs <= 0
            || !double.IsFinite(request.StartMs + request.DurationMs))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "An export needs a finite, positive timeline range.");
        }
        if (request.Mode is not ("Exact" or "Fast"))
        {
            throw new ArgumentException("The export mode must be Exact or Fast.", nameof(request));
        }
        if (request.Mode == "Exact" && request.EncodingThreads is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Exact export encoding threads must be between 1 and 128.");
        }
        var spans = BuildSpans(request);
        var details = new Dictionary<string, MediaProbeDetails>(StringComparer.Ordinal);
        foreach (var span in spans.Where(span => span.Path is not null))
        {
            EnsureDifferentOutput(span.Path!, request.OutputPath);
            if (!details.TryGetValue(span.Path!, out var probe))
            {
                probe = await ProbeDetailsAsync(span.Path!, cancellationToken);
                details.Add(span.Path!, probe);
            }
            span.Details = probe;
            if (probe.Probe.VideoCodec.Length == 0)
            {
                throw new InvalidDataException("The selected source does not contain a video stream.");
            }
        }
        if (request.Mode == "Fast")
        {
            if (spans.Any(span => span.Path is null)
                || spans.Select(span => span.Details!.CodecSignature).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                throw new InvalidOperationException("Fast export cannot preserve gaps or join incompatible media. Select Exact mode.");
            }
            if (!request.KeyframeBoundsPrepared)
            {
                await AlignFastBoundsAsync(spans, cancellationToken);
            }
        }
        foreach (var span in spans.Where(span => span.Path is not null))
        {
            await PrepareIndexedSpanAsync(span, cancellationToken);
        }

        var output = Path.GetFullPath(request.OutputPath);
        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        if (File.Exists(output))
        {
            throw new IOException("The export destination already exists.");
        }
        var work = Path.Combine(directory, ".export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var partial = Path.Combine(work, "result.mp4");
        try
        {
            var totalDuration = spans.Sum(span => span.DurationMs);
            var firstProbe = spans.FirstOrDefault(span => span.Details is not null)?.Details?.Probe;
            var frameRate = firstProbe is { FrameRate: > 0 and <= 120 } ? firstProbe.FrameRate : 30;
            if (request.Mode == "Fast" && CanCopyContinuousInput(spans, totalDuration))
            {
                await processes.RunAsync(MediaProcessKind.Export,
                [
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1", "-copyts", "-f", "flv", "-i", "pipe:0",
                    "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-avoid_negative_ts", "make_zero",
                    "-movflags", "+faststart", "-f", "mp4", "-y", partial
                ], cancellationToken, (stream, token) => CopyContinuousInputAsync(spans, stream, token),
                    new CallbackProgress<double>(value => progress.Report(value * 0.98)), TimeSpan.FromMilliseconds(totalDuration));
            }
            else
            {
                var width = Math.Max(64, (firstProbe?.Width ?? 1280) / 2 * 2);
                var height = Math.Max(32, (firstProbe?.Height ?? 720) / 2 * 2);
                var slate = Path.Combine(work, "gap.ppm");
                if (spans.Any(span => span.Path is null))
                {
                    await GapSlateRenderer.WriteAsync(slate, width, height, cancellationToken);
                }
                var concat = new StringBuilder("ffconcat version 1.0\n");
                var processedDuration = 0d;
                for (var index = 0; index < spans.Count; index++)
                {
                    var span = spans[index];
                    var part = Path.Combine(work, $"part{index:D5}.ts");
                    var partProgressStart = processedDuration;
                    var partProgress = new CallbackProgress<double>(value => progress.Report((partProgressStart + span.DurationMs * value) / totalDuration * 0.9));
                    var arguments = request.Mode == "Fast"
                        ? BuildFastArguments(span, part)
                        : BuildExactArguments(span, part, slate, width, height, frameRate, request.EncodingThreads);
                    Func<Stream, CancellationToken, Task>? input = span.Range is { } range
                        ? (stream, token) => IndexedFlvRange.CopyAsync(span.Path!, range, stream, token)
                        : null;
                    await processes.RunAsync(MediaProcessKind.Export, arguments, cancellationToken, input, partProgress, TimeSpan.FromMilliseconds(span.DurationMs));
                    concat.Append("file '").Append(EscapeConcatPath(part)).Append("'\n");
                    concat.Append("duration ").Append(Seconds(span.DurationMs)).Append('\n');
                    processedDuration += span.DurationMs;
                }
                var concatPath = Path.Combine(work, "parts.ffconcat");
                await File.WriteAllTextAsync(concatPath, concat.ToString(), new UTF8Encoding(false), cancellationToken);
                await processes.RunAsync(MediaProcessKind.Export,
                [
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1", "-f", "concat", "-safe", "0", "-i", concatPath,
                    "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-t", Seconds(totalDuration),
                    "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", "-f", "mp4", "-y", partial
                ], cancellationToken, progress: new CallbackProgress<double>(value => progress.Report(0.9 + value * 0.08)), expectedDuration: TimeSpan.FromMilliseconds(totalDuration));
            }
            var resultProbe = await ProbeAsync(partial, cancellationToken);
            var bytes = new FileInfo(partial).Length;
            if (bytes == 0 || resultProbe.VideoCodec.Length == 0 || resultProbe.DurationMs <= 0)
            {
                throw new InvalidDataException("The export process produced no playable video.");
            }
            if (request.Mode == "Exact" && Math.Abs(resultProbe.DurationMs - request.DurationMs) > Math.Max(150, 1000 / frameRate + 75))
            {
                throw new InvalidDataException("The exported duration does not match the requested wall-clock range.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, output, overwrite: false);
            progress.Report(1);
            return new()
            {
                ActualStartMs = spans[0].SessionStartMs,
                ActualEndMs = spans[^1].SessionStartMs + spans[^1].DurationMs,
                Bytes = bytes
            };
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static bool CanCopyContinuousInput(IReadOnlyList<ExportSpan> spans, double durationMs)
    {
        return durationMs <= uint.MaxValue && spans.Zip(spans.Skip(1))
            .All(pair => Math.Abs(pair.First.SessionStartMs + pair.First.DurationMs - pair.Second.SessionStartMs) < 0.001)
            && spans.All(span => span.Range is { } range
            && Math.Abs(range.StartMs - span.SourceStartMs) < 0.001
            && Math.Abs(range.EndMs - span.SourceStartMs - span.DurationMs) < 0.001);
    }

    private static async Task CopyContinuousInputAsync(IReadOnlyList<ExportSpan> spans, Stream output, CancellationToken cancellationToken)
    {
        var start = spans[0].SessionStartMs;
        for (var index = 0; index < spans.Count; index++)
        {
            var span = spans[index];
            await IndexedFlvRange.AppendAsync(span.Path!, span.Range!, output, span.SessionStartMs - start,
                index == 0, cancellationToken);
        }
    }

    private async Task<MediaProbeDetails> ProbeDetailsAsync(string path, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync(MediaProcessKind.Probe,
        ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_data", Path.GetFullPath(path)], cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var streams = root.TryGetProperty("streams", out var streamArray) ? streamArray.EnumerateArray().ToArray() : [];
        var video = streams.FirstOrDefault(stream => Text(stream, "codec_type") == "video");
        var audio = streams.FirstOrDefault(stream => Text(stream, "codec_type") == "audio");
        var duration = root.TryGetProperty("format", out var format) ? Number(format, "duration") * 1000 : 0;
        var probe = new MediaProbe
        {
            DurationMs = duration,
            VideoCodec = Text(video, "codec_name"),
            AudioCodec = Text(audio, "codec_name"),
            Width = (int)Number(video, "width"),
            Height = (int)Number(video, "height"),
            FrameRate = Ratio(Text(video, "avg_frame_rate")),
            HasAudio = audio.ValueKind != JsonValueKind.Undefined,
            VideoBitrate = (long)Number(video, "bit_rate"),
            AudioBitrate = (long)Number(audio, "bit_rate"),
            SourceStartMs = Number(video, "start_time") * 1000,
            IsHdr = Text(video, "color_transfer") is "smpte2084" or "arib-std-b67"
        };
        if (probe.FrameRate == 0)
        {
            probe.FrameRate = Ratio(Text(video, "r_frame_rate"));
        }
        var signature = string.Join('|', new[]
        {
            Text(video, "codec_name"), Text(video, "profile"), Text(video, "level"), Text(video, "width"), Text(video, "height"),
            Text(video, "pix_fmt"), Text(video, "sample_aspect_ratio"), Text(video, "r_frame_rate"), Text(video, "extradata"),
            Text(audio, "codec_name"), Text(audio, "profile"), Text(audio, "sample_rate"), Text(audio, "channels"), Text(audio, "channel_layout"), Text(audio, "extradata")
        });
        return new(probe, signature);
    }

    private static List<ExportSpan> BuildSpans(MediaExportRequest request)
    {
        var spans = new List<ExportSpan>();
        var cursor = request.StartMs;
        var end = request.StartMs + request.DurationMs;
        foreach (var input in request.Inputs.OrderBy(input => input.SessionStartMs))
        {
            if (!double.IsFinite(input.DurationMs) || input.DurationMs <= 0 || !double.IsFinite(input.SessionStartMs)
                || !double.IsFinite(input.SourceStartMs) || input.SourceStartMs < 0)
            {
                throw new ArgumentException("Every export input must have a finite, positive range.", nameof(request));
            }
            var start = Math.Max(request.StartMs, input.SessionStartMs);
            var stop = Math.Min(end, input.SessionStartMs + input.DurationMs);
            if (stop <= start)
            {
                continue;
            }
            if (start < cursor - 0.1)
            {
                throw new ArgumentException("Export input ranges overlap.", nameof(request));
            }
            if (start > cursor + 0.1)
            {
                spans.Add(new() { SessionStartMs = cursor, DurationMs = start - cursor });
            }
            spans.Add(new()
            {
                Path = input.Path,
                SessionStartMs = start,
                SourceStartMs = input.SourceStartMs + start - input.SessionStartMs,
                DurationMs = stop - start,
                Range = input.Range
            });
            cursor = stop;
        }
        if (spans.Count == 0)
        {
            throw new ArgumentException("The export range has no known timeline spans.", nameof(request));
        }
        if (cursor < end - 0.1)
        {
            spans.Add(new() { SessionStartMs = cursor, DurationMs = end - cursor });
        }
        return spans;
    }

    private static async Task AlignFastBoundsAsync(List<ExportSpan> spans, CancellationToken cancellationToken)
    {
        for (var index = 0; index < spans.Count; index++)
        {
            var span = spans[index];
            var indexPath = Path.ChangeExtension(span.Path!, ".index.jsonl");
            if (!File.Exists(indexPath))
            {
                throw new InvalidOperationException("Fast export requires a complete recording keyframe index. Select Exact mode.");
            }
            var source = await FlvIndexSnapshot.ReadAsync(indexPath, new FileInfo(span.Path!).Length, cancellationToken);
            var start = index == 0
                ? source.Keyframes.LastOrDefault(frame => frame.TimestampMs <= span.SourceStartMs)?.TimestampMs ?? 0
                : span.SourceStartMs;
            var requestedEnd = span.SourceStartMs + span.DurationMs;
            var end = index == spans.Count - 1
                ? source.Keyframes.FirstOrDefault(frame => frame.TimestampMs >= requestedEnd)?.TimestampMs ?? source.DurationMs
                : requestedEnd;
            if (end <= start)
            {
                throw new InvalidOperationException("No complete keyframe range is available for fast export. Select Exact mode.");
            }
            span.SessionStartMs += start - span.SourceStartMs;
            span.SourceStartMs = start;
            span.DurationMs = end - start;
        }
    }

    private static async Task PrepareIndexedSpanAsync(ExportSpan span, CancellationToken cancellationToken)
    {
        if (span.Range is not null)
        {
            return;
        }
        var path = Path.ChangeExtension(span.Path!, ".index.jsonl");
        if (!File.Exists(path))
        {
            return;
        }
        span.Range = await IndexedFlvRange.PlanAsync(path, new FileInfo(span.Path!).Length, span.SourceStartMs, span.DurationMs, cancellationToken);
    }

    private static List<string> BuildFastArguments(ExportSpan span, string output)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1" };
        arguments.AddRange(span.Range is not null
            ? ["-f", "flv", "-i", "pipe:0"]
            : ["-ss", Seconds(span.SourceStartMs), "-i", span.Path!]);
        arguments.AddRange(
        [
            "-map", "0:v:0", "-map", "0:a:0?", "-t", Seconds(span.DurationMs), "-c", "copy", "-avoid_negative_ts", "make_zero", "-f", "mpegts", "-y", output
        ]);
        return arguments;
    }

    private static List<string> BuildExactArguments(ExportSpan span, string output, string slate, int width, int height, double frameRate,
        int encodingThreads)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1" };
        if (span.Path is null)
        {
            arguments.AddRange(["-loop", "1", "-framerate", frameRate.ToString(CultureInfo.InvariantCulture), "-i", slate]);
        }
        else
        {
            if (span.Range is not null)
            {
                arguments.AddRange(["-f", "flv", "-i", "pipe:0"]);
            }
            else
            {
                arguments.AddRange(["-ss", Seconds(span.SourceStartMs), "-i", span.Path]);
            }
        }
        if (span.Details?.Probe.HasAudio != true)
        {
            arguments.AddRange(["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);
        }
        var audioInput = span.Details?.Probe.HasAudio == true ? "0:a:0" : "1:a:0";
        if (span.Range is { } range)
        {
            arguments.AddRange(["-ss", Seconds(Math.Max(0, span.SourceStartMs - range.StartMs))]);
        }
        arguments.AddRange(
        [
            "-map", "0:v:0", "-map", audioInput, "-t", Seconds(span.DurationMs),
            "-vf", $"setpts=PTS-STARTPTS,scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={frameRate.ToString(CultureInfo.InvariantCulture)},format=yuv420p",
            "-af", $"asetpts=PTS-STARTPTS,aresample=48000,aformat=channel_layouts=stereo,apad,atrim=duration={Seconds(span.DurationMs)}",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-threads:v", encodingThreads.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p",
            "-g", Math.Max(1, (int)Math.Round(frameRate * 2)).ToString(CultureInfo.InvariantCulture), "-sc_threshold", "0",
            "-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2", "-f", "mpegts", "-y", output
        ]);
        return arguments;
    }

    private static string Seconds(double milliseconds) => (milliseconds / 1000).ToString("0.#########", CultureInfo.InvariantCulture);
    private static string EscapeConcatPath(string path) => Path.GetFullPath(path).Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal);

    private static void EnsureDifferentOutput(string input, string output)
    {
        if (Path.GetFullPath(input).Equals(Path.GetFullPath(output), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("A derivative must not overwrite its canonical source.");
        }
    }

    private static string Text(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value.ToString() : "";
    }

    private static double Number(JsonElement element, string property)
    {
        return double.TryParse(Text(element, property), CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
    }

    private static double Ratio(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || !double.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator)
            || !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator) || denominator == 0)
        {
            return 0;
        }
        return numerator / denominator;
    }
}
