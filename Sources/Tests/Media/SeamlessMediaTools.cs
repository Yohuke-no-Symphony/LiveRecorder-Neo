using System.Globalization;
using System.Text.Json;
using Xunit;

namespace Yohuke.LiveRecorder.Media.Tests;

internal static class SeamlessMediaTools
{
    internal static async Task<string> CreateSampleAsync(MediaFixture fixture, string frameRate, int sampleRate, int gopFrames)
    {
        var path = Path.Combine(fixture.DirectoryPath, "continuous-source.flv");
        await MediaProcessRunner.RunProcessAsync("ffmpeg",
        [
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=" + frameRate,
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=" + sampleRate.ToString(CultureInfo.InvariantCulture),
            "-t", "12", "-c:v", "libx264", "-preset", "medium", "-threads", "1", "-bf", "2", "-b_strategy", "0",
            "-g", gopFrames.ToString(CultureInfo.InvariantCulture), "-keyint_min", gopFrames.ToString(CultureInfo.InvariantCulture),
            "-sc_threshold", "0", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "96k", "-avoid_negative_ts", "make_zero",
            "-f", "flv", "-y", path
        ], CancellationToken.None);
        var result = await MediaProcessRunner.RunProcessAsync("ffprobe",
        [
            "-v", "error", "-show_streams", "-show_entries", "stream=codec_type,has_b_frames,sample_rate,r_frame_rate",
            "-of", "json", path
        ], CancellationToken.None);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = Assert.Single(streams, stream => stream.GetProperty("codec_type").GetString() == "video");
        var audio = Assert.Single(streams, stream => stream.GetProperty("codec_type").GetString() == "audio");
        Assert.Equal(2, video.GetProperty("has_b_frames").GetInt32());
        Assert.Equal(frameRate == "25" ? "25/1" : frameRate, video.GetProperty("r_frame_rate").GetString());
        Assert.Equal(sampleRate.ToString(CultureInfo.InvariantCulture), audio.GetProperty("sample_rate").GetString());
        return path;
    }

    internal static async Task<SeamlessMediaPacket[]> PacketsAsync(string path)
    {
        var packets = new List<SeamlessMediaPacket>();
        await MediaProcessRunner.RunProcessAsync("ffprobe",
        [
            "-v", "error", "-show_packets", "-show_data_hash", "sha256", "-show_entries",
            "packet=codec_type,dts_time,pts_time,data_hash", "-of", "compact=p=0:nk=0", path
        ], CancellationToken.None, outputLine: line =>
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }
            var values = line.Split('|').Select(field => field.Split('=', 2)).ToDictionary(field => field[0], field => field[1], StringComparer.Ordinal);
            packets.Add(new(values["codec_type"], values["data_hash"],
                decimal.Parse(values["dts_time"], CultureInfo.InvariantCulture) * 1000,
                decimal.Parse(values["pts_time"], CultureInfo.InvariantCulture) * 1000));
        });
        Assert.Contains(packets, packet => packet.StreamKind == "video");
        Assert.Contains(packets, packet => packet.StreamKind == "audio");
        return packets.ToArray();
    }

    internal static async Task<string> ConcatAsync(MediaFixture fixture, IEnumerable<string> paths)
    {
        var list = Path.Combine(fixture.DirectoryPath, "continuous.ffconcat");
        await File.WriteAllLinesAsync(list, paths.Select(path => "file '" + path.Replace("\\", "/", StringComparison.Ordinal)
            .Replace("'", "'\\''", StringComparison.Ordinal) + "'"));
        var output = Path.Combine(fixture.DirectoryPath, "concatenated.flv");
        await MediaProcessRunner.RunProcessAsync("ffmpeg",
        [
            "-hide_banner", "-loglevel", "error", "-copyts", "-f", "concat", "-safe", "0", "-auto_convert", "0", "-i", list,
            "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-avoid_negative_ts", "make_zero", "-f", "flv", "-y", output
        ], CancellationToken.None);
        return output;
    }
}
