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
        var keyframeFrames = (int)Math.Clamp(Math.Ceiling(frameRate * profile.KeyframeIntervalSeconds), 1, int.MaxValue);
        var filter = FormattableString.Invariant($"scale={width}:{height}:flags=bicubic,setsar=1,fps={rate}");
        if (encoder.Encoder == "h264_vaapi")
        {
            filter += ",format=nv12,hwupload";
        }
        var segmentPath = Path.Combine(outputDirectory, profile.SingleFile ? "media.mp4" : "chunk_%05d.m4s")
            .Replace(Path.DirectorySeparatorChar, '/');
        var playlistPath = Path.Combine(outputDirectory, "index.m3u8").Replace(Path.DirectorySeparatorChar, '/');
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
            "-g", keyframeFrames.ToString(CultureInfo.InvariantCulture),
            "-force_key_frames", FormattableString.Invariant($"expr:gte(t,n_forced*{profile.KeyframeIntervalSeconds})"), "-flags", "+cgop",
            "-c:a", "aac", "-b:a", Math.Max(1, audioBitrate).ToString(CultureInfo.InvariantCulture),
            "-ar", "48000", "-ac", "2", "-threads:a", "1", "-af", "aresample=async=1:first_pts=0",
            "-avoid_negative_ts", "make_zero", "-f", "hls", "-hls_time", profile.SegmentDurationSeconds.ToString(CultureInfo.InvariantCulture), "-hls_segment_type", "fmp4",
            "-hls_playlist_type", "vod", "-hls_flags", profile.SingleFile ? "independent_segments+single_file" : "independent_segments+temp_file",
            "-hls_segment_options", "use_editlist=0",
            "-hls_fmp4_init_filename", "init.mp4", "-hls_segment_filename", segmentPath,
            playlistPath
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
