using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>将高频录制回调合并为本地清单和低频数据库提交。</summary>
public sealed class RecordingProgressSink : IProgress<MediaRecordingUpdate>, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly BroadcastSession session;
    private readonly string attemptId;
    private readonly string directory;
    private readonly IMongoContext context;
    private readonly IStatusPublisher publisher;
    private readonly ILogger logger;
    private readonly Dictionary<string, RecordingSegment> segments;
    private readonly CancellationTokenSource stop = new();
    private readonly Task persister;
    private readonly RecordingSessionProjectionService? projection;
    private readonly string? captureId;
    private readonly string relativePathPrefix;
    private readonly RecordingSessionLifecycle? lifecycle;
    private readonly TimeProvider clock;
    private bool dirty;
    private readonly HashSet<string> dirtySegments = [];

    /// <summary>从已有分段恢复同一场次的进度。</summary>
    public RecordingProgressSink(BroadcastSession session, IEnumerable<RecordingSegment> existing, string attemptId, string directory, IMongoContext context, IStatusPublisher publisher, ILogger logger,
        RecordingSessionProjectionService? projection = null, string? captureId = null, string relativePathPrefix = "",
        RecordingSessionLifecycle? lifecycle = null, TimeProvider? timeProvider = null)
    {
        this.session = session;
        this.attemptId = attemptId;
        this.directory = directory;
        this.context = context;
        this.publisher = publisher;
        this.logger = logger;
        this.projection = projection;
        this.captureId = captureId;
        this.relativePathPrefix = relativePathPrefix;
        this.lifecycle = lifecycle;
        clock = timeProvider ?? TimeProvider.System;
        segments = existing.ToDictionary(x => x.Id);
        persister = PersistLoopAsync();
    }

    /// <inheritdoc />
    public void Report(MediaRecordingUpdate value)
    {
        lock (gate)
        {
            if (!segments.TryGetValue(value.SegmentId, out var segment))
            {
                segment = new RecordingSegment
                {
                    Id = value.SegmentId,
                    AssetId = value.SegmentId,
                    SessionId = session.Id,
                    AttemptId = attemptId,
                    RelativePath = Path.Combine(relativePathPrefix, value.RelativePath),
                    IndexRelativePath = Path.Combine(relativePathPrefix, value.IndexRelativePath),
                    StartOffsetMs = value.StartOffsetMs
                };
                segments.Add(segment.Id, segment);
            }
            if (value.Kind == "Committed" && value.CommittedBytes > segment.CommittedBytes && value.DurationMs > segment.DurationMs)
            {
                var received = value.LastMediaReceivedAtUtc ?? value.CommitSnapshot?.LastMediaReceivedAtUtc ?? clock.GetUtcNow().UtcDateTime;
                if (lifecycle is not null)
                {
                    lifecycle.ReportMedia(received);
                    session.Lifecycle = lifecycle.State;
                    session.LastMediaReceivedAtUtc = session.Lifecycle.LastMediaReceivedAtUtc;
                }
                else if (session.LastMediaReceivedAtUtc is null || received > session.LastMediaReceivedAtUtc)
                {
                    session.LastMediaReceivedAtUtc = received;
                }
            }
            segment.CommittedBytes = Math.Max(segment.CommittedBytes, value.CommittedBytes);
            segment.DurationMs = Math.Max(segment.DurationMs, value.DurationMs);
            segment.IsClosed |= value.Kind == "Closed";
            segment.Codec = value.Codec;
            session.LastMediaAtUtc = session.StartedAtUtc.AddMilliseconds(segments.Values
                .Where(x => x.DurationMs > 0).Select(x => x.StartOffsetMs + x.DurationMs).DefaultIfEmpty(0).Max());
            session.TotalBytes = segments.Values.Sum(x => x.CommittedBytes);
            dirty = true;
            dirtySegments.Add(segment.Id);
        }
    }

    /// <summary>提交已合并的媒体和生命周期事实，不等待后台批处理周期。</summary>
    public Task FlushAsync() => PersistAsync(true);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await persister;
        await PersistAsync(true);
        stop.Dispose();
    }

    private async Task PersistLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                await PersistAsync(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private async Task PersistAsync(bool force)
    {
        SessionManifest manifest;
        List<RecordingSegment> changed;
        lock (gate)
        {
            if (!dirty && !force)
            {
                return;
            }
            manifest = new SessionManifest { Session = session, Segments = segments.Values.Select(Clone).ToList() };
            changed = manifest.Segments.Where(x => dirtySegments.Contains(x.Id)).ToList();
            dirtySegments.Clear();
            dirty = false;
        }
        if (projection is null)
        {
            await SessionManifestFiles.WriteAsync(Path.Combine(directory, "场次.json"), manifest);
        }
        try
        {
            if (projection is not null && captureId is not null)
            {
                await context.Collection<BroadcastSession>("sessions").UpdateOneAsync(value => value.Id == session.Id,
                    Builders<BroadcastSession>.Update.Set(value => value.Error, session.Error).Set(value => value.ErrorCode, session.ErrorCode)
                        .Set(value => value.ErrorArguments, session.ErrorArguments));
                await projection.PublishLocalAsync(session, manifest.Segments, captureId, false, CancellationToken.None);
                return;
            }
            foreach (var segment in changed)
            {
                await context.Collection<RecordingSegment>("segments").ReplaceOneAsync(x => x.Id == segment.Id, segment, new ReplaceOptions { IsUpsert = true });
            }
            await context.Collection<BroadcastSession>("sessions").UpdateOneAsync(x => x.Id == session.Id,
                Builders<BroadcastSession>.Update.Set(x => x.LastMediaAtUtc, session.LastMediaAtUtc).Set(x => x.TotalBytes, session.TotalBytes)
                    .Set(x => x.LastMediaReceivedAtUtc, session.LastMediaReceivedAtUtc).Set(x => x.Lifecycle, session.Lifecycle)
                    .Set(x => x.Error, session.Error).Set(x => x.ErrorCode, session.ErrorCode).Set(x => x.ErrorArguments, session.ErrorArguments)
                    .Set(x => x.State, session.State).Set(x => x.HasDanmu, session.HasDanmu));
            await publisher.PublishAsync("session", session.Id);
        }
        catch (Exception error)
        {
            lock (gate)
            {
                dirty = true;
                foreach (var segment in changed)
                {
                    dirtySegments.Add(segment.Id);
                }
            }
            logger.LogWarning("场次 {SessionId} 已保存在文件中，数据库同步等待恢复：{Message}", session.Id, error.Message);
        }
    }

    private static RecordingSegment Clone(RecordingSegment value) => new()
    {
        Id = value.Id,
        CreatedAtUtc = value.CreatedAtUtc,
        SessionId = value.SessionId,
        AssetId = value.AssetId,
        PendingTranscodeProfileIds = value.PendingTranscodeProfileIds.ToList(),
        AttemptId = value.AttemptId,
        RelativePath = value.RelativePath,
        IndexRelativePath = value.IndexRelativePath,
        StartOffsetMs = value.StartOffsetMs,
        DurationMs = value.DurationMs,
        CommittedBytes = value.CommittedBytes,
        IsClosed = value.IsClosed,
        Revision = value.Revision,
        Codec = value.Codec,
        RootRelativePath = value.RootRelativePath,
        RootIndexRelativePath = value.RootIndexRelativePath
    };
}
