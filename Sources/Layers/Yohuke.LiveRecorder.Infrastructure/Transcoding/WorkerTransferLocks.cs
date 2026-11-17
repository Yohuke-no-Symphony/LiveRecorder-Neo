namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

internal static class WorkerTransferLocks
{
    private static SemaphoreSlim[] Gates { get; } = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    internal static SemaphoreSlim Gate(string kind, string jobId) =>
        Gates[(int)(Convert.ToUInt32(TranscodeAssetRegistry.Hash(kind + ":" + jobId)[..8], 16) % (uint)Gates.Length)];
}
