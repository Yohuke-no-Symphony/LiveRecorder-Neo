using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

internal static class WorkerConfigurationValidation
{
    internal static void Validate(WorkerConfiguration configuration, string kind = "Transcoding")
    {
        WorkerConfigurationPolicy.Validate(configuration, kind);
        if (string.IsNullOrWhiteSpace(configuration.TempDirectory) || !Path.IsPathFullyQualified(configuration.TempDirectory))
        {
            throw new InvalidDataException(kind switch
            {
                "Recording" => "Configure an absolute persistent recording spool directory using 'config'.",
                "AI" => "Configure an absolute AI cache directory using 'config'.",
                _ => "Configure FFmpeg, ffprobe and an absolute temporary directory using 'config'."
            });
        }
    }
}
