using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Encoding;

internal sealed class TranscodeBitrateException(string message, TranscodeMediaInfo media) : Exception(message)
{
    internal TranscodeMediaInfo Media { get; } = media;
}
