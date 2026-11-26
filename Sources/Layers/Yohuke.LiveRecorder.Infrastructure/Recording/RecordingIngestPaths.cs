using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

internal static class RecordingIngestPaths
{
    internal static string UploadLockKey(string sessionId) => "recording-ingest-" + sessionId;

    internal static void RequireId(string value)
    {
        if (value.Length is < 1 or > 128 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new InfrastructureException("录像归档标识无效。");
        }
    }

    internal static void RequireHash(string value)
    {
        if (value.Length != 64 || !value.All(char.IsAsciiHexDigit))
        {
            throw new InfrastructureException("录像归档校验值无效。");
        }
    }

    internal static string Staging(IAppPaths paths, RecordingSessionReceipt session) =>
        MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(".work", "recording", session.AssignmentId, session.Id));

    internal static string SegmentRelative(string segmentId) => Path.Combine("录像", segmentId + ".flv");
    internal static string SegmentIndexRelative(string segmentId) => Path.Combine("录像", segmentId + ".index.jsonl");
    internal static string Segment(IAppPaths paths, RecordingSessionReceipt session, string segmentId) =>
        MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(session.DirectoryPath, SegmentRelative(segmentId)));

    internal static string SegmentIndex(IAppPaths paths, RecordingSessionReceipt session, string segmentId) =>
        MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(session.DirectoryPath, SegmentIndexRelative(segmentId)));

    internal static string Artifact(IAppPaths paths, RecordingSessionReceipt session, string segmentId, string kind, string? hash)
    {
        if (kind == "media")
        {
            return Segment(paths, session, segmentId);
        }
        var staging = Staging(paths, session);
        return kind switch
        {
            "index" => Path.Combine(staging, segmentId, "index.upload"),
            "prefix" => Path.Combine(staging, segmentId, "prefixes", hash! + ".blob"),
            "events" => Path.Combine(staging, "events.upload"),
            "event-index" => Path.Combine(staging, "event-indexes", hash! + ".jsonl"),
            _ => throw new InfrastructureException("录像归档文件类型无效。")
        };
    }
}
