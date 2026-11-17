using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Encoding;

internal static class EncoderArguments
{
    internal static readonly string[] ENCODERS = ["h264_nvenc", "h264_qsv", "h264_amf", "h264_vaapi", "h264_videotoolbox", "libx264"];

    internal static List<string> Device(EncoderCapability encoder)
    {
        var result = new List<string>();
        var device = encoder.DeviceId;
        if (encoder.Encoder == "h264_vaapi")
        {
            result.AddRange(["-vaapi_device", device ?? "/dev/dri/renderD128"]);
        }
        else if (encoder.Encoder == "h264_qsv")
        {
            var platform = OperatingSystem.IsWindows() ? "child_device_type=d3d11va" : "child_device_type=vaapi";
            if (!string.IsNullOrWhiteSpace(device))
            {
                if (device.Contains(',') || device.Contains(':'))
                {
                    throw new InvalidDataException("Invalid QSV device identifier");
                }
                platform += ",child_device=" + device;
            }
            result.AddRange(["-init_hw_device", "qsv=worker:hw," + platform]);
        }
        else if (encoder.Encoder == "h264_amf" && !string.IsNullOrWhiteSpace(device))
        {
            if (!int.TryParse(device, out var index) || index < 0)
            {
                throw new InvalidDataException("The AMF device identifier must be a non-negative adapter index");
            }
            result.AddRange(["-init_hw_device", $"d3d11va=worker:{index}", "-filter_hw_device", "worker"]);
        }
        return result;
    }

    internal static List<string> Encoding(EncoderCapability encoder, int threads)
    {
        if (!ENCODERS.Contains(encoder.Encoder, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Unsupported encoder");
        }
        var result = new List<string> { "-c:v", encoder.Encoder, "-profile:v", "high" };
        switch (encoder.Encoder)
        {
            case "h264_nvenc":
                result.AddRange(["-preset", "p4", "-rc", "vbr", "-forced-idr", "1"]);
                if (!string.IsNullOrWhiteSpace(encoder.DeviceId))
                {
                    result.AddRange(["-gpu", encoder.DeviceId]);
                }
                break;
            case "h264_qsv":
                result.AddRange(["-preset", "medium", "-forced_idr", "1"]);
                break;
            case "h264_amf":
                result.AddRange(["-quality", "balanced", "-rc", "vbr_peak"]);
                break;
            case "h264_videotoolbox":
                result.AddRange(["-allow_sw", "0"]);
                break;
            case "libx264":
                result.AddRange(["-preset", "medium", "-threads:v", Math.Max(1, threads).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-x264-params", "open-gop=0:scenecut=0:lookahead-threads=1"]);
                break;
        }
        return result;
    }
}
