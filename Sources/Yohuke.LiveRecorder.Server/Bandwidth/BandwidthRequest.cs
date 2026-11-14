namespace Yohuke.LiveRecorder.Server.Bandwidth;

internal sealed class BandwidthRequest(MediaBandwidthTransfer transfer, int maximumBytes)
{
    internal MediaBandwidthTransfer Transfer { get; } = transfer;
    internal int MaximumBytes { get; } = maximumBytes;
    internal TaskCompletionSource<MediaBandwidthPermit> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal LinkedListNode<BandwidthRequest>? Node { get; set; }
}
