using System.Globalization;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Encoding;

internal static class TranscodePlanner
{
    internal static TranscodePlan Create(string sourcePath, string outputDirectory, TranscodeMediaInfo source,
        TranscodeProfile profile, EncoderCapability encoder, int threads)
    {
        TranscodeConfigurationPolicy.ValidateProfile(profile);
        if (source.IsHdr)
        {
            throw new NotSupportedException("HdrUnsupported");
        }
        if (profile.Height < 2 || profile.VideoBitrateKbps <= 0 || profile.AudioBitrateKbps <= 0
            || !double.IsFinite(source.FrameRate) || source.FrameRate <= 0 || source.Width < 2 || source.Height < 2)
        {
            throw new InvalidDataException("Invalid transcoding profile or input media parameters");
        }
        var shortEdge = Math.Min(source.Width, source.Height);
        if (profile.Height > shortEdge)
        {
            throw new NotSupportedException("ResolutionAboveSource");
        }
        if (source.VideoBitrate <= 0 || (source.HasAudio && source.AudioBitrate <= 0))
        {
            throw new NotSupportedException("SourceBitrateUnknown");
        }
        var scale = (double)profile.Height / shortEdge;
        var height = Math.Max(2, (int)Math.Floor(source.Height * scale / 2) * 2);
        var width = Math.Max(2, (int)Math.Floor(source.Width * scale / 2) * 2);
        var videoBitrate = Math.Min(profile.VideoBitrateKbps * 1000L, source.VideoBitrate);
        var audioBitrate = Math.Min(profile.AudioBitrateKbps * 1000L, source.AudioBitrate);
        var frameRate = Math.Min(source.FrameRate, profile.MaxFrameRate ?? source.FrameRate);
        if (!double.IsFinite(frameRate) || frameRate <= 0)
        {
            throw new InvalidDataException("The target frame rate must be a finite positive number");
        }
        var rate = frameRate.ToString("0.########", CultureInfo.InvariantCulture);
        var filter = FormattableString.Invariant($"scale={width}:{height}:flags=bicubic,setsar=1,fps={rate}");
        if (encoder.Encoder == "h264_vaapi")
        {
            filter += ",format=nv12,hwupload";
        }
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1", "-threads", "1" };
        arguments.AddRange(EncoderArguments.Device(encoder));
        arguments.AddRange(["-i", sourcePath, "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn", "-filter_threads", "1", "-filter_complex_threads", "1", "-vf", filter]);
        arguments.AddRange(EncoderArguments.Encoding(encoder, threads));
        if (encoder.Encoder != "h264_vaapi")
        {
            arguments.AddRange(["-pix_fmt", "yuv420p"]);
        }
        arguments.AddRange([
            "-b:v", videoBitrate.ToString(CultureInfo.InvariantCulture),
            "-maxrate:v", videoBitrate.ToString(CultureInfo.InvariantCulture),
            "-bufsize:v", (videoBitrate * 2).ToString(CultureInfo.InvariantCulture),
            "-g", ((int)Math.Ceiling(frameRate * 2)).ToString(CultureInfo.InvariantCulture),
            "-force_key_frames", "expr:gte(t,n_forced*2)", "-flags", "+cgop",
            "-c:a", "aac", "-b:a", Math.Max(1, audioBitrate).ToString(CultureInfo.InvariantCulture),
            "-ar", "48000", "-ac", "2", "-threads:a", "1", "-af", "aresample=async=1:first_pts=0",
            "-avoid_negative_ts", "make_zero", "-f", "hls", "-hls_time", "6", "-hls_segment_type", "fmp4",
            "-hls_playlist_type", "vod", "-hls_flags", "independent_segments+temp_file",
            "-hls_segment_options", "use_editlist=0",
            "-hls_fmp4_init_filename", "init.mp4", "-hls_segment_filename", Path.Combine(outputDirectory, "chunk_%05d.m4s"),
            Path.Combine(outputDirectory, "index.m3u8")
        ]);
        return new(arguments, new()
        {
            Width = width,
            Height = height,
            FrameRate = frameRate,
            DurationMs = source.DurationMs,
            HasAudio = source.HasAudio,
            AudioCodec = source.HasAudio ? "aac" : "",
            VideoBitrate = videoBitrate,
            AudioBitrate = source.HasAudio ? audioBitrate : 0,
            VideoCodec = "h264",
            SourceStartMs = source.SourceStartMs
        });
    }
}
