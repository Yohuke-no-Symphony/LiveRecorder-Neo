using Yohuke.LiveRecorder.Application.Localization;
using System.Globalization;
using System.Text.Json;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Recording;

namespace Yohuke.LiveRecorder.Infrastructure.Library;

/// <summary>通过稳定文件引用归组和按物理边界拆分，保留所有源档案。</summary>
public sealed class SessionGroupingService(IMongoContext context, LibraryService library, ISettingsStore settingsStore, IStatusPublisher publisher, IAppPaths paths)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>将同主播、不重叠的已结束场次归为一个目录条目。</summary>
    public async Task<BroadcastSession> GroupAsync(string id, IReadOnlyCollection<string> otherIds, string? title, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
            var ids = otherIds.Append(id).Distinct().ToList();
            if (ids.Count is < 2 or > 100)
            {
                throw new InfrastructureException(MessageCode.GROUPING_COUNT_INVALID);
            }
            var details = new List<SessionDetail>();
            foreach (var sourceId in ids)
            {
                var detail = await RequireEditableAsync(sourceId, cancellationToken);
                details.Add(detail);
            }
            details = details.OrderBy(x => x.Session.StartedAtUtc).ToList();
            if (details.Select(x => x.Session.StreamerId).Distinct().Count() != 1)
            {
                throw new InfrastructureException(MessageCode.GROUPING_STREAMER_MISMATCH);
            }
            for (var index = 1; index < details.Count; index++)
            {
                if (details[index].Session.StartedAtUtc < details[index - 1].Session.EndedAtUtc!.Value)
                {
                    throw new InfrastructureException(MessageCode.GROUPING_SESSIONS_OVERLAP, 422);
                }
            }
            var first = details[0].Session;
            var result = Projection(first, string.IsNullOrWhiteSpace(title) ? first.Title : title.Trim(), first.StartedAtUtc, details.Max(x => x.Session.EndedAtUtc!.Value), ids);
            result.HasDanmu = details.Any(x => x.Session.HasDanmu);
            var sourceBookmarks = await context.Collection<SessionBookmark>("sessionBookmarks")
                .Find(Builders<SessionBookmark>.Filter.In(x => x.SessionId, ids)).ToListAsync(cancellationToken);
            var bookmarksBySession = sourceBookmarks.ToLookup(x => x.SessionId);
            var bookmarks = new List<SessionBookmark>();
            var segments = new List<RecordingSegment>();
            foreach (var detail in details)
            {
                var offset = (detail.Session.StartedAtUtc - result.StartedAtUtc).TotalMilliseconds;
                segments.AddRange(detail.Segments.Select(x => ProjectSegment(x, detail.Session, result, offset)));
                bookmarks.AddRange(bookmarksBySession[detail.Session.Id].Select(x => ProjectBookmark(x, result, offset)));
                result.EventSources.AddRange(Sources(detail.Session).Select(x => new EventArchiveSource
                {
                    DirectoryPath = x.DirectoryPath,
                    StartOffsetMs = x.StartOffsetMs + offset,
                    FromMs = x.FromMs,
                    ToMs = x.ToMs
                }));
            }
            result.EventSources = result.EventSources.OrderBy(x => x.StartOffsetMs + x.FromMs).ToList();
            if (result.EventSources.Count > 1000)
            {
                throw new InfrastructureException(MessageCode.GROUPING_TOO_MANY_ARCHIVES);
            }
            await PublishBatchAsync(new ProjectionOperation
            {
                Outputs = [new SessionManifest { Session = result, Segments = segments }],
                SourceIds = ids,
                Bookmarks = bookmarks
            }, cancellationToken);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>在所选物理片段开始处分为两个场次。</summary>
    public async Task<IReadOnlyList<BroadcastSession>> SplitAsync(string id, string beforeSegmentId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
            var detail = await RequireEditableAsync(id, cancellationToken);
            var boundary = detail.Segments.FirstOrDefault(x => x.Id == beforeSegmentId) ?? throw new InfrastructureException(MessageCode.GROUPING_BOUNDARY_NOT_FOUND);
            var point = boundary.StartOffsetMs;
            if (point <= 0 || point >= detail.DurationMs || detail.Segments.Any(x => x.StartOffsetMs < point && x.StartOffsetMs + x.DurationMs > point))
            {
                throw new InfrastructureException(MessageCode.GROUPING_BOUNDARY_OVERLAP, 422);
            }
            var original = detail.Session;
            var left = Projection(original, original.Title + "_上", original.StartedAtUtc, original.StartedAtUtc.AddMilliseconds(point), [id]);
            var right = Projection(original, original.Title + "_下", original.StartedAtUtc.AddMilliseconds(point), original.EndedAtUtc!.Value, [id]);
            var sourceBookmarks = await context.Collection<SessionBookmark>("sessionBookmarks")
                .Find(x => x.SessionId == id).ToListAsync(cancellationToken);
            var bookmarks = sourceBookmarks.Select(bookmark => bookmark.OffsetMs < point
                ? ProjectBookmark(bookmark, left, 0)
                : ProjectBookmark(bookmark, right, -point)).ToList();
            var leftSegments = detail.Segments.Where(x => x.StartOffsetMs < point).Select(x => ProjectSegment(x, original, left, 0)).ToList();
            var rightSegments = detail.Segments.Where(x => x.StartOffsetMs >= point).Select(x => ProjectSegment(x, original, right, -point)).ToList();
            if (leftSegments.Count == 0 || rightSegments.Count == 0)
            {
                throw new InfrastructureException(MessageCode.GROUPING_VIDEO_REQUIRED_BOTH_SIDES);
            }
            foreach (var source in Sources(original))
            {
                var end = source.ToMs ?? detail.DurationMs - source.StartOffsetMs;
                if (source.FromMs + source.StartOffsetMs < point)
                {
                    left.EventSources.Add(new EventArchiveSource { DirectoryPath = source.DirectoryPath, StartOffsetMs = source.StartOffsetMs, FromMs = source.FromMs, ToMs = Math.Min(end, point - source.StartOffsetMs) });
                }
                if (end + source.StartOffsetMs > point)
                {
                    right.EventSources.Add(new EventArchiveSource { DirectoryPath = source.DirectoryPath, StartOffsetMs = source.StartOffsetMs - point, FromMs = Math.Max(source.FromMs, point - source.StartOffsetMs), ToMs = end });
                }
            }
            await PublishBatchAsync(new ProjectionOperation
            {
                Outputs = [new SessionManifest { Session = left, Segments = leftSegments }, new SessionManifest { Session = right, Segments = rightSegments }],
                SourceIds = [id],
                Bookmarks = bookmarks
            }, cancellationToken);
            return new[] { left, right };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>重建源场次的隐藏状态，恢复发布后中断的收尾。</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var journalRoot = Path.Combine(paths.RecordsDirectory, ".work", "groups");
        if (Directory.Exists(journalRoot))
        {
            foreach (var file in Directory.EnumerateFiles(journalRoot, "operation.json", SearchOption.AllDirectories))
            {
                await using var input = File.OpenRead(file);
                var operation = await JsonSerializer.DeserializeAsync<ProjectionOperation>(input, JsonFiles.Options, cancellationToken);
                if (operation is not null)
                {
                    await CompleteBatchAsync(operation, cancellationToken);
                }
                input.Close();
                File.Delete(file);
            }
        }
        var projections = await context.Collection<BroadcastSession>("sessions").Find(x => x.GroupedSessionIds.Count > 0 && x.ProjectionReady).ToListAsync(cancellationToken);
        var hidden = projections.SelectMany(x => x.GroupedSessionIds).Distinct().ToList();
        if (hidden.Count > 0)
        {
            await context.Collection<BroadcastSession>("sessions").UpdateManyAsync(Builders<BroadcastSession>.Filter.In(x => x.Id, hidden), Builders<BroadcastSession>.Update.Set(x => x.IsHidden, true), cancellationToken: cancellationToken);
        }
    }

    /// <summary>返回原场次或已经投影的事件来源。</summary>
    public static IReadOnlyList<EventArchiveSource> Sources(BroadcastSession session)
    {
        return session.EventSources.Count > 0 ? session.EventSources : new[]
        {
            new EventArchiveSource { DirectoryPath = session.DirectoryPath, FromMs = 0, ToMs = session.EndedAtUtc is { } end ? (end - session.StartedAtUtc).TotalMilliseconds : null }
        };
    }

    private async Task<SessionDetail> RequireEditableAsync(string id, CancellationToken cancellationToken)
    {
        var detail = await library.GetAsync(id, cancellationToken);
        if (detail.Session.EndedAtUtc is null || detail.Session.IsHidden || detail.Segments.Any(x => !x.IsClosed))
        {
            throw new InfrastructureException(MessageCode.GROUPING_SESSION_NOT_EDITABLE, 409);
        }
        if (await context.Collection<MediaJob>("jobs").Find(x => x.MutationKey == id).AnyAsync(cancellationToken))
        {
            throw new InfrastructureException(MessageCode.GROUPING_REPAIR_IN_PROGRESS, 409);
        }
        return detail;
    }

    private static BroadcastSession Projection(BroadcastSession original, string title, DateTime start, DateTime end, List<string> sourceIds) => new()
    {
        CreatedAtUtc = start,
        StreamerId = original.StreamerId,
        RoomId = original.RoomId,
        Title = title,
        CoverUrl = original.CoverUrl,
        CoverRelativePath = original.CoverRelativePath,
        LiveTime = original.LiveTime,
        StartedAtUtc = start,
        EndedAtUtc = end,
        LastMediaAtUtc = end,
        State = "Completed",
        HasDanmu = original.HasDanmu,
        ProjectionReady = false,
        GroupedSessionIds = sourceIds
    };

    private static RecordingSegment ProjectSegment(RecordingSegment source, BroadcastSession original, BroadcastSession target, double offset) => new()
    {
        Id = target.Id + "_" + source.Id,
        CreatedAtUtc = source.CreatedAtUtc,
        SessionId = target.Id,
        AttemptId = source.AttemptId,
        RelativePath = source.RelativePath,
        IndexRelativePath = source.IndexRelativePath,
        RootRelativePath = source.RootRelativePath ?? Path.Combine(original.DirectoryPath, source.RelativePath),
        RootIndexRelativePath = source.RootIndexRelativePath ?? (string.IsNullOrEmpty(source.IndexRelativePath) ? null : Path.Combine(original.DirectoryPath, source.IndexRelativePath)),
        StartOffsetMs = source.StartOffsetMs + offset,
        DurationMs = source.DurationMs,
        CommittedBytes = source.CommittedBytes,
        IsClosed = true,
        Revision = 1,
        Codec = source.Codec
    };

    private static SessionBookmark ProjectBookmark(SessionBookmark source, BroadcastSession target, double offset) => new()
    {
        Id = target.Id + "_" + source.Id,
        CreatedAtUtc = source.CreatedAtUtc,
        SessionId = target.Id,
        OwnerId = source.OwnerId,
        Title = source.Title,
        OffsetMs = source.OffsetMs + offset
    };

    private async Task PrepareAsync(BroadcastSession session, List<RecordingSegment> segments, ServerSettings settings, CancellationToken cancellationToken)
    {
        var streamer = await context.Collection<Streamer>("streamers").Find(x => x.Id == session.StreamerId).FirstOrDefaultAsync(cancellationToken);
        var baseDirectory = streamer?.DirectoryName;
        if (string.IsNullOrEmpty(baseDirectory))
        {
            baseDirectory = "归档_" + session.StreamerId;
        }
        var local = TimeZoneInfo.ConvertTimeFromUtc(session.StartedAtUtc, TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone));
        session.DirectoryPath = Path.Combine(baseDirectory, local.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + "_" + MediaPaths.SafeName(session.Title) + "_" + session.Id[..8]);
        session.TotalBytes = segments.Sum(x => x.CommittedBytes);
    }

    private async Task PublishBatchAsync(ProjectionOperation operation, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        foreach (var output in operation.Outputs)
        {
            await PrepareAsync(output.Session, output.Segments, settings, cancellationToken);
        }
        var journal = Path.Combine(paths.RecordsDirectory, ".work", "groups", Guid.NewGuid().ToString("N"), "operation.json");
        await JsonFiles.WriteAsync(journal, operation, cancellationToken);
        await CompleteBatchAsync(operation, cancellationToken);
        File.Delete(journal);
    }

    private async Task CompleteBatchAsync(ProjectionOperation operation, CancellationToken cancellationToken)
    {
        foreach (var output in operation.Outputs)
        {
            var existing = await context.Collection<BroadcastSession>("sessions").Find(x => x.Id == output.Session.Id).FirstOrDefaultAsync(cancellationToken);
            if (existing is { ProjectionReady: true })
            {
                output.Session = existing;
                output.Segments = await context.Collection<RecordingSegment>("segments").Find(x => x.SessionId == existing.Id && x.Revision == existing.MediaRevision).ToListAsync(cancellationToken);
                continue;
            }
            foreach (var segment in output.Segments)
            {
                await context.Collection<RecordingSegment>("segments").ReplaceOneAsync(x => x.Id == segment.Id, segment, new ReplaceOptions { IsUpsert = true }, cancellationToken);
            }
            foreach (var bookmark in operation.Bookmarks.Where(x => x.SessionId == output.Session.Id))
            {
                await context.Collection<SessionBookmark>("sessionBookmarks").ReplaceOneAsync(x => x.Id == bookmark.Id, bookmark, new ReplaceOptions { IsUpsert = true }, cancellationToken);
            }
            await JsonFiles.WriteAsync(Path.Combine(MediaPaths.Resolve(paths.RecordsDirectory, output.Session.DirectoryPath), "场次.json"), output, cancellationToken);
            await context.Collection<BroadcastSession>("sessions").ReplaceOneAsync(x => x.Id == output.Session.Id, output.Session, new ReplaceOptions { IsUpsert = true }, cancellationToken);
        }
        foreach (var output in operation.Outputs)
        {
            output.Session.ProjectionReady = true;
            await context.Collection<BroadcastSession>("sessions").UpdateOneAsync(x => x.Id == output.Session.Id, Builders<BroadcastSession>.Update.Set(x => x.ProjectionReady, true), cancellationToken: cancellationToken);
            await JsonFiles.WriteAsync(Path.Combine(MediaPaths.Resolve(paths.RecordsDirectory, output.Session.DirectoryPath), "场次.json"), output, cancellationToken);
        }
        foreach (var id in operation.SourceIds)
        {
            await context.Collection<BroadcastSession>("sessions").UpdateOneAsync(x => x.Id == id, Builders<BroadcastSession>.Update.Set(x => x.IsHidden, true), cancellationToken: cancellationToken);
        }
        foreach (var output in operation.Outputs)
        {
            await publisher.PublishAsync("session", output.Session.Id, cancellationToken: cancellationToken);
        }
    }
}
