namespace Yohuke.LiveRecorder.Transcoding.Worker.Transport;

internal sealed class WorkerLeaseLostException(string message) : OperationCanceledException(message);
