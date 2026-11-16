using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

internal static class WorkerConfigurationValidation
{
    internal static void Validate(WorkerConfiguration configuration, string kind = "Transcoding")
    {
        WorkerConfigurationPolicy.Validate(configuration, kind);
        if (string.IsNullOrWhiteSpace(configuration.TempDirectory) || !Path.IsPathFullyQualified(configuration.TempDirectory))
        {
            throw new InvalidDataException("Configure FFmpeg, ffprobe and an absolute temporary directory using 'config'.");
        }
    }
}
