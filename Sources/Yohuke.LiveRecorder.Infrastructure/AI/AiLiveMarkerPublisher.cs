using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Application.Library.LiveMarkers;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Domain.LiveMarkers;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;

namespace Yohuke.LiveRecorder.Infrastructure.AI;

/// <summary>把完整、证据校验通过的独立生成结果幂等追加为原生共享直播记录。</summary>
public sealed class AiLiveMarkerPublisher(LibraryService library, IAppPaths paths, LiveMarkerFileStore files, LiveMarkerYamlParser parser)
{
    /// <summary>使用任务身份确定文件身份；相同任务的恢复不会产生重复文件。</summary>
    public static string FileId(string jobId) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("ai-live-markers:" + jobId)).AsSpan(0, 16)).ToString("N");

    /// <summary>在媒体库锁和最新生命周期约束下原子发布，保留全部已有附件。</summary>
    public async Task<string> PublishAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        if (job.Purpose != AiJobPurposes.LIVE_MARKERS || !job.Result.IsComplete || job.RequestInFlight ||
            job.Context.Segments.Count == 0 || job.Context.Segments.Any(segment => segment.State is not ("Completed" or "ReviewedNoTopics" or "Empty")) ||
            string.IsNullOrEmpty(job.OwnerId) || job.Snapshot.StartedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw AiErrors.CreateException("AI_LIVE_MARKER_RESULT_INVALID", 409);
        }
        var document = CreateDocument(job);
        var bytes = Serialize(document);
        document = parser.Parse(bytes, TimeZoneInfo.Utc);
        LiveMarkerYamlParser.ValidateAlignment(document, 0);
        var id = FileId(job.Id);
        var metadata = new LiveMarkerFileMetadata
        {
            Id = id, OriginId = id, GeneratedByAiJobId = job.Id,
            OriginalFileName = "AI直播记录_" + job.Snapshot.StartedAtUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + id[..8] + ".yaml",
            UploaderId = job.OwnerId, UploaderName = job.OwnerName, UploadedAtUtc = job.CreatedAtUtc,
            EntryCount = document.Entries.Count, CalibrationMs = 0, SourceTimeZoneId = TimeZoneInfo.Utc.Id,
            ContentHash = LiveMarkerFileStore.Hash(bytes)
        };
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var detail = await library.GetAsync(job.SessionId, cancellationToken);
        if (detail.Session.IsHidden || detail.Session.IsTrashed || !detail.Session.ProjectionReady ||
            detail.Session.StartedAtUtc != job.Snapshot.StartedAtUtc)
        {
            throw new InfrastructureException(MessageCode.LIVE_MARKER_SESSION_NOT_AVAILABLE, 409);
        }
        await LiveMarkerProjectionGuard.EnsureWritableAsync(paths.RecordsDirectory, job.SessionId, cancellationToken);
        await LiveMarkerProjectionGuard.EnsureNoTrashIntentAsync(paths.RecordsDirectory, job.SessionId, cancellationToken);
        LiveMarkerFileMetadata? existing;
        try
        {
            existing = await files.ReadMetadataAsync(detail.Session.DirectoryPath, id, cancellationToken);
        }
        catch (InfrastructureException error) when (error.Code == MessageCode.LIVE_MARKER_NOT_FOUND.ToString())
        {
            existing = null;
        }
        if (existing is not null)
        {
            if (existing.GeneratedByAiJobId != job.Id || existing.OriginId != id || existing.ContentHash != metadata.ContentHash ||
                existing.UploaderId != job.OwnerId || existing.EntryCount != document.Entries.Count)
            {
                throw new InfrastructureException(MessageCode.LIVE_MARKER_FILE_CORRUPT, 409);
            }
            var saved = await files.ReadDocumentAsync(detail.Session.DirectoryPath, existing, cancellationToken);
            await files.ReadOriginalAsync(detail.Session.DirectoryPath, existing, cancellationToken);
            if (saved.StartTime != document.StartTime || !saved.Entries.SequenceEqual(document.Entries))
            {
                throw new InfrastructureException(MessageCode.LIVE_MARKER_FILE_CORRUPT, 409);
            }
            return id;
        }
        await files.PublishAsync(detail.Session.DirectoryPath, bytes, document, metadata, cancellationToken);
        return id;
    }

    /// <summary>只读检查已生成文件是否仍存在；用户删除后不会重新创建。</summary>
    public async Task<bool> ExistsAsync(string sessionId, string? fileId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(fileId))
        {
            return false;
        }
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var detail = await library.GetAsync(sessionId, cancellationToken);
        try
        {
            var file = await files.ReadMetadataAsync(detail.Session.DirectoryPath, fileId, cancellationToken);
            await files.ReadDocumentAsync(detail.Session.DirectoryPath, file, cancellationToken);
            await files.ReadOriginalAsync(detail.Session.DirectoryPath, file, cancellationToken);
            return true;
        }
        catch (InfrastructureException error) when (error.Code == MessageCode.LIVE_MARKER_NOT_FOUND.ToString())
        {
            return false;
        }
    }

    private static LiveMarkerDocument CreateDocument(AiAnalysisJob job)
    {
        if (job.Result.LiveMarkers.Count > LiveMarkerLimits.MAXIMUM_ENTRIES)
        {
            throw new InfrastructureException(MessageCode.LIVE_MARKER_TOO_MANY_ENTRIES);
        }
        var start = new DateTimeOffset(job.Snapshot.StartedAtUtc);
        var entries = new List<LiveMarkerEntry>();
        foreach (var draft in job.Result.LiveMarkers.OrderBy(item => item.OffsetMs).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            var anchor = draft.Evidence.FirstOrDefault(item => item.Id == draft.AnchorEvidenceId);
            if (anchor is null || !double.IsFinite(draft.OffsetMs) || draft.OffsetMs < 0 || draft.OffsetMs >= job.Snapshot.DurationMs ||
                anchor.OffsetMs != draft.OffsetMs || string.IsNullOrWhiteSpace(draft.Message) || draft.Message.Length > 160)
            {
                throw AiErrors.CreateException("AI_LIVE_MARKER_RESULT_INVALID", 409);
            }
            entries.Add(new()
            {
                RealDateTime = start.AddMilliseconds(draft.OffsetMs), LiveTime = TimeSpan.FromMilliseconds(draft.OffsetMs),
                Message = draft.Message, MarkerColor = anchor.Kind == "SuperChat" ? LiveMarkerColor.Orange : LiveMarkerColor.Blue
            });
        }
        return new() { StartTime = start, Entries = entries };
    }

    private static byte[] Serialize(LiveMarkerDocument document)
    {
        var markers = new YamlSequenceNode(document.Entries.Select(entry => new YamlMappingNode
        {
            { "RealDateTime", Scalar(entry.RealDateTime.ToString("O", CultureInfo.InvariantCulture)) },
            { "LiveTime", Scalar(entry.LiveTime.ToString("c", CultureInfo.InvariantCulture)) },
            { "Message", Scalar(entry.Message) },
            { "MarkerColor", Scalar(entry.MarkerColor.ToString()) }
        }));
        var root = new YamlMappingNode { { "StartTime", Scalar(document.StartTime.ToString("O", CultureInfo.InvariantCulture)) }, { "Marker", markers } };
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(root)).Save(output, false);
        return new UTF8Encoding(false).GetBytes(output.ToString());
    }

    private static YamlScalarNode Scalar(string value) => new(value) { Style = ScalarStyle.DoubleQuoted };
}
