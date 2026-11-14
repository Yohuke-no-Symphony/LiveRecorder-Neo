namespace Yohuke.LiveRecorder.Server.Bandwidth;

internal sealed class BandwidthUserQueue(BandwidthAccount account)
{
    internal BandwidthAccount Account { get; } = account;
    internal LinkedList<BandwidthRequest> Requests { get; } = new();
    internal LinkedListNode<BandwidthUserQueue>? Node { get; set; }
    internal int Deficit { get; set; }
}
