using System.Globalization;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Library.LiveMarkers;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain.LiveMarkers;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;

/// <summary>在最新场次状态、媒体库锁和上传归属约束下维护共享直播记录。</summary>
public sealed class SessionLiveMarkerService(
    LibraryService library,
    ISettingsStore settingsStore,
    IAppPaths paths,
    LiveMarkerFileStore files,
    LiveMarkerYamlParser parser,
    IMongoContext context) : ISessionLiveMarkerService
{
    /// <inheritdoc/>
    public async Task<LiveMarkerFileListResponse> ListAsync(string sessionId, LiveMarkerActor actor,
        CancellationToken cancellationToken = default)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var detail = await RequireAccessibleAsync(sessionId, cancellationToken);
        var result = await files.ListAsync(detail.Session.DirectoryPath, cancellationToken);
        var settings = await settingsStore.GetAsync(cancellationToken);
        return new(result.Select(file => Summary(file, actor)).ToArray(), settings.TimeZone);
    }

    /// <inheritdoc/>
    public async Task<LiveMarkerFileResponse> GetAsync(string sessionId, string fileId, LiveMarkerActor actor,
        CancellationToken cancellationToken = default)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var detail = await RequireAccessibleAsync(sessionId, cancellationToken);
        var file = await files.ReadMetadataAsync(detail.Session.DirectoryPath, fileId, cancellationToken);
        var document = await files.ReadDocumentAsync(detail.Session.DirectoryPath, file, cancellationToken);
        var entries = document.Entries.Select((entry, index) => new LiveMarkerEntryResponse(index,
                entry.RealDateTime.ToString("O", CultureInfo.InvariantCulture),
                entry.LiveTime.ToString("c", CultureInfo.InvariantCulture),
                entry.Message, entry.MarkerColor.ToString(),
                (entry.RealDateTime.UtcDateTime - detail.Session.StartedAtUtc).TotalMilliseconds + file.CalibrationMs))
            .OrderBy(entry => entry.OffsetMs).ThenBy(entry => entry.Index).ToArray();
        return new(Summary(file, actor), document.StartTime.ToString("O", CultureInfo.InvariantCulture), entries);
    }

    /// <inheritdoc/>
    public async Task<LiveMarkerFileSummary> UploadAsync(string sessionId, Stream content, string fileName,
        string? timeZoneId,
        long calibrationMs, LiveMarkerActor actor, CancellationToken cancellationToken = default)
    {
        await RequireAccessibleAsync(sessionId, cancellationToken);
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 ||
            !(Path.GetExtension(name).Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
              Path.GetExtension(name).Equals(".yml", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InfrastructureException(MessageCode.LIVE_MARKER_FILE_INVALID);
        }

        var settings = await settingsStore.GetAsync(cancellationToken);
        var zoneId = string.IsNullOrWhiteSpace(timeZoneId) ? settings.TimeZone : timeZoneId.Trim();
        var zone = LiveMarkerYamlParser.ResolveTimeZone(zoneId);
        var bytes = await ReadUploadAsync(content, cancellationToken);
        var document = parser.Parse(bytes, zone);
        LiveMarkerYamlParser.ValidateAlignment(document, calibrationMs);
        var metadata = new LiveMarkerFileMetadata
        {
            OriginalFileName = name,
            UploaderId = actor.Id,
            UploaderName = actor.Name,
            EntryCount = document.Entries.Count,
            CalibrationMs = calibrationMs,
            SourceTimeZoneId = zone.Id,
            ContentHash = LiveMarkerFileStore.Hash(bytes)
        };
        metadata = metadata with { OriginId = metadata.Id };
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var detail = await RequireWritableAsync(sessionId, cancellationToken);
        await files.PublishAsync(detail.Session.DirectoryPath, bytes, document, metadata, cancellationToken);
        return Summary(metadata, actor);
    }

    /// <inheritdoc/>
    public async Task<LiveMarkerFileSummary> AlignAsync(string sessionId, string fileId, long calibrationMs,
        LiveMarkerActor actor, CancellationToken cancellationToken = default)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var detail = await RequireWritableAsync(sessionId, cancellationToken);
        var metadata = await files.ReadMetadataAsync(detail.Session.DirectoryPath, fileId, cancellationToken);
        RequireManager(metadata, actor);
        var document = await files.ReadDocumentAsync(detail.Session.DirectoryPath, metadata, cancellationToken);
        LiveMarkerYamlParser.ValidateAlignment(document, calibrationMs);
        var updated = metadata with { CalibrationMs = calibrationMs };
        await files.WriteMetadataAsync(detail.Session.DirectoryPath, updated, cancellationToken);
        return Summary(updated, actor);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string sessionId, string fileId, LiveMarkerActor actor,
        CancellationToken cancellationToken = default)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var detail = await RequireWritableAsync(sessionId, cancellationToken);
        var metadata = await files.ReadMetadataAsync(detail.Session.DirectoryPath, fileId, cancellationToken);
        RequireManager(metadata, actor);
        if (metadata.GeneratedByAiJobId is { } jobId && await context.Collection<AiAnalysisJob>("ai_analysis_jobs")
                .Find(job => job.Id == jobId && job.State == "Running" && job.PublicationStarted).AnyAsync(cancellationToken))
        {
            throw AiErrors.CreateException("AI_PUBLICATION_IN_PROGRESS", 409);
        }
        cancellationToken.ThrowIfCancellationRequested();
        files.Delete(detail.Session.DirectoryPath, fileId);
    }

    private async Task<SessionDetail> RequireAccessibleAsync(string sessionId, CancellationToken cancellationToken)
    {
        var detail = await library.GetAsync(sessionId, cancellationToken);
        if (detail.Session.IsHidden || detail.Session.IsTrashed || !detail.Session.ProjectionReady)
        {
            throw new InfrastructureException(MessageCode.LIVE_MARKER_SESSION_NOT_AVAILABLE, 409);
        }

        return detail;
    }

    private async Task<SessionDetail> RequireWritableAsync(string sessionId, CancellationToken cancellationToken)
    {
        var detail = await RequireAccessibleAsync(sessionId, cancellationToken);
        await LiveMarkerProjectionGuard.EnsureWritableAsync(paths.RecordsDirectory, sessionId, cancellationToken);
        await LiveMarkerProjectionGuard.EnsureNoTrashIntentAsync(paths.RecordsDirectory, sessionId, cancellationToken);
        return detail;
    }

    private static LiveMarkerFileSummary Summary(LiveMarkerFileMetadata file, LiveMarkerActor actor)
    {
        return new(file.Id, file.OriginalFileName, file.UploaderId, file.UploaderName, file.UploadedAtUtc,
            file.EntryCount,
            file.CalibrationMs, file.SourceTimeZoneId, actor.CanManageOthers || file.UploaderId == actor.Id);
    }

    private static void RequireManager(LiveMarkerFileMetadata file, LiveMarkerActor actor)
    {
        if (!actor.CanManageOthers && file.UploaderId != actor.Id)
        {
            throw new InfrastructureException(MessageCode.LIBRARY_ACCESS_DENIED, 403);
        }
    }

    private static async Task<byte[]> ReadUploadAsync(Stream content, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        while (true)
        {
            var read = await content.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > LiveMarkerLimits.MAXIMUM_BYTES)
            {
                throw new InfrastructureException(MessageCode.LIVE_MARKER_FILE_TOO_LARGE, 413);
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
