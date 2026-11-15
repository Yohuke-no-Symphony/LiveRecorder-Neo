namespace Yohuke.LiveRecorder.Transcoding.Worker.Execution;

internal readonly record struct WorkerEncodingProgress(double ProcessedDurationMs, double? FramesPerSecond, double? Speed);
