using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Encoding;

internal sealed record SourceMediaCache
{
    public int Version { get; init; }
    public TranscodeMediaInfo? Media { get; init; }
}
