namespace Yohuke.LiveRecorder.Transcoding.Worker.Scheduling;

internal sealed class WorkerCapacityUnavailableException(string message) : IOException(message);
