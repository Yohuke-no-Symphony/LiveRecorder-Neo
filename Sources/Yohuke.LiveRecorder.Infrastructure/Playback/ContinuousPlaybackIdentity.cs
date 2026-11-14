namespace Yohuke.LiveRecorder.Infrastructure.Playback;

internal sealed record ContinuousPlaybackIdentity(string Key, string SourcePath, string IndexPath,
    long SourceLength, long SourceWriteTicks, long IndexLength, long IndexWriteTicks)
{
    internal bool MatchesFiles()
    {
        var source = new FileInfo(SourcePath);
        var index = new FileInfo(IndexPath);
        return source.Exists && index.Exists && source.Length == SourceLength && source.LastWriteTimeUtc.Ticks == SourceWriteTicks
            && index.Length == IndexLength && index.LastWriteTimeUtc.Ticks == IndexWriteTicks;
    }
}
