namespace Yohuke.LiveRecorder.Transcoding.Worker.Services;

internal sealed class WorkerServiceException(string message, Exception? innerException = null) : Exception(message, innerException)
{
}
