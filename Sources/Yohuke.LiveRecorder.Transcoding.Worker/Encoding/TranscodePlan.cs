using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Encoding;

internal sealed record TranscodePlan(List<string> Arguments, TranscodeMediaInfo ExpectedMedia);
