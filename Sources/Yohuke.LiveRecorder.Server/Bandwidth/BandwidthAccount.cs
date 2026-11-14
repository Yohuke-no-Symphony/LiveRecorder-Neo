namespace Yohuke.LiveRecorder.Server.Bandwidth;

internal sealed class BandwidthAccount(string id, BandwidthBucket downloadBucket)
{
    internal string Id { get; } = id;
    internal BandwidthBucket DownloadBucket { get; } = downloadBucket;
    internal int Transfers { get; set; }
    internal int Permits { get; set; }
    internal long LastActivity { get; set; }
}
