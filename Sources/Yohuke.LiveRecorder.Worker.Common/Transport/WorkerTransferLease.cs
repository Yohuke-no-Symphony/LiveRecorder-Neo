namespace Yohuke.LiveRecorder.Transcoding.Worker.Transport;

internal sealed record WorkerTransferLease
{
    internal required string JobId { get; init; }
    internal required int Attempt { get; init; }
    internal required string LeaseToken { get; init; }
}
