using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Storage;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>Receives durable recording spools and exposes only coherent media, prefix and event commit boundaries.</summary>
public sealed class RecordingIngestService(IMongoContext context, IAppPaths paths, ISettingsStore settingsStore,
    RecordingOwnershipService ownership, IStatusPublisher publisher) : IDisposable
{
    private const int MAX_CHUNK_BYTES = 8 * 1024 * 1024;
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>Creates or seals a stable remote broadcast without overwriting an existing local or different-owner session.</summary>
    public async Task RegisterSessionAsync(string workerId, string assignmentId, int generation, string token,
        RecordingSessionDescriptor descriptor, CancellationToken cancellationToken)
    {
        RecordingIngestPaths.RequireId(descriptor.SessionId);
        if (descriptor.RoomId <= 0 || descriptor.StartedAtUtc == default || descriptor.Title.Length > 1024
            || descriptor.StreamerName.Length > 256 || descriptor.IsClosed && descriptor.EndedAtUtc is null
            || descriptor.EndedAtUtc < descriptor.StartedAtUtc)
        {
            throw new InfrastructureException("录像场次描述无效。");
        }
        var gate = Gate(descriptor.SessionId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var ingestLock = await MediaJobExecutionLock.AcquireAsync(paths, "recording-ingest-" + descriptor.SessionId, cancellationToken);
            await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
            var assignment = await ownership.RequireAsync(workerId, assignmentId, generation, token, cancellationToken, true);
            if (descriptor.RoomId != assignment.RoomId)
            {
                throw new InfrastructureException("录像场次不属于当前房间。", 409);
            }
            var receipt = await Sessions.Find(value => value.Id == descriptor.SessionId).FirstOrDefaultAsync(cancellationToken);
            if (receipt is null)
            {
                if (assignment.State == "Released" || descriptor.IsClosed || await context.Collection<BroadcastSession>("sessions").Find(value => value.Id == descriptor.SessionId).AnyAsync(cancellationToken))
                {
                    throw new InfrastructureException("录像场次身份已被使用。", 409);
                }
                var target = await context.Collection<Streamer>("streamers").Find(value => value.Id == assignment.TargetId).FirstOrDefaultAsync(cancellationToken);
                var settings = await settingsStore.GetAsync(cancellationToken);
                var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
                var folder = string.IsNullOrEmpty(target?.DirectoryName)
                    ? MediaPaths.SafeName(descriptor.StreamerName.Length == 0 ? assignment.RoomId.ToString(CultureInfo.InvariantCulture) : descriptor.StreamerName) + "_" + descriptor.StreamerUid
                    : target.DirectoryName;
                var date = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(descriptor.StartedAtUtc, DateTimeKind.Utc), zone)
                    .ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
                receipt = new()
                {
                    Id = descriptor.SessionId, AssignmentId = assignmentId, Generation = generation, Descriptor = descriptor,
                    DirectoryPath = Path.Combine(folder, date + "_" + MediaPaths.SafeName(descriptor.Title) + "_" + descriptor.SessionId[..Math.Min(8, descriptor.SessionId.Length)])
                };
                Directory.CreateDirectory(MediaPaths.Resolve(paths.RecordsDirectory, receipt.DirectoryPath));
                await Sessions.InsertOneAsync(receipt, cancellationToken: cancellationToken);
                if (target is not null)
                {
                    await context.Collection<Streamer>("streamers").UpdateOneAsync(value => value.Id == target.Id,
                        Builders<Streamer>.Update.Set(value => value.DirectoryName, folder).Set(value => value.Name, descriptor.StreamerName)
                            .Set(value => value.Uid, descriptor.StreamerUid), cancellationToken: cancellationToken);
                }
            }
            else
            {
                RequireSessionIdentity(receipt, assignment, descriptor.SessionId);
                if (receipt.Descriptor.StartedAtUtc.Ticks / TimeSpan.TicksPerMillisecond != descriptor.StartedAtUtc.Ticks / TimeSpan.TicksPerMillisecond
                    || receipt.Descriptor.LiveTime != descriptor.LiveTime)
                {
                    throw new InfrastructureException("录像场次固定时间身份已改变。", 409);
                }
                if (receipt.Descriptor.IsClosed && !descriptor.IsClosed)
                {
                    return;
                }
                descriptor = descriptor with { HasDanmu = receipt.Descriptor.HasDanmu || descriptor.HasDanmu };
                if (await IsLibraryUnavailableAsync(receipt, cancellationToken))
                {
                    if (receipt.Descriptor.IsClosed && descriptor.IsClosed)
                    {
                        return;
                    }
                    throw new InfrastructureException("录像已被移除或投影到其它场次。", 410);
                }
                if (descriptor.IsClosed)
                {
                    var segments = await Segments.Find(value => value.SessionId == receipt.Id).ToListAsync(cancellationToken);
                    if (segments.Count == 0 || segments.Any(value => value.Commit is not { IsClosed: true })
                        || descriptor.HasDanmu && receipt.Events is not { IsClosed: true })
                    {
                        throw new InfrastructureException("录像场次只能在媒体与弹幕完整封口后关闭。", 409);
                    }
                }
                receipt.Descriptor = descriptor;
                await Sessions.UpdateOneAsync(value => value.Id == receipt.Id && value.AssignmentId == assignmentId,
                    Builders<RecordingSessionReceipt>.Update.Set(value => value.Descriptor, descriptor), cancellationToken: cancellationToken);
            }
            await PublishSessionAsync(assignment, receipt, cancellationToken);
            await CleanupFinalizedAsync(receipt, cancellationToken);
            if (receipt.Descriptor.IsClosed && receipt.FinalizedAtUtc is null && await IsSealedAsync(receipt, cancellationToken))
            {
                receipt.FinalizedAtUtc = DateTime.UtcNow;
                await Sessions.UpdateOneAsync(value => value.Id == receipt.Id && value.FinalizedAtUtc == null,
                    Builders<RecordingSessionReceipt>.Update.Set(value => value.FinalizedAtUtc, receipt.FinalizedAtUtc),
                    cancellationToken: cancellationToken);
                await publisher.PublishAsync("session", receipt.Id, cancellationToken: cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Returns the durable tail offset of a growing artifact or a fixed immutable blob.</summary>
    public async Task<long> GetOffsetAsync(string workerId, string assignmentId, int generation, string token, string sessionId,
        string segmentId, string kind, string? hash, long length, CancellationToken cancellationToken)
    {
        var gate = Gate(sessionId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var ingestLock = await MediaJobExecutionLock.AcquireAsync(paths, "recording-ingest-" + sessionId, cancellationToken);
            var assignment = await ownership.RequireAsync(workerId, assignmentId, generation, token, cancellationToken, true);
            var session = await RequireSessionAsync(assignment, sessionId, cancellationToken);
            ValidateArtifact(segmentId, kind, hash, length);
            if (kind is "media" or "index" or "prefix")
            {
                var segment = await RequireSegmentAsync(assignment, session, segmentId, cancellationToken, assignment.State != "Released");
                if (segment.Commit is { IsClosed: true } closed)
                {
                    if (kind == "media")
                    {
                        return closed.MediaCommittedBytes;
                    }
                    if (kind == "index")
                    {
                        return closed.IndexCommittedBytes;
                    }
                    if (kind == "prefix" && string.Equals(hash, closed.PrefixSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return closed.PrefixLength;
                    }
                }
            }
            if (session.Events is { IsClosed: true } events)
            {
                if (kind == "events")
                {
                    return events.CommittedBytes;
                }
                if (kind == "event-index" && string.Equals(hash, events.IndexSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return events.IndexCommittedBytes;
                }
            }
            await RequireActiveLibraryAsync(session, cancellationToken);
            var path = RecordingIngestPaths.Artifact(paths, session, segmentId, kind, hash);
            if (kind is "prefix" or "event-index")
            {
                await RequireBlobLengthAsync(path, length, cancellationToken);
            }
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Appends one bounded chunk; growing artifacts accept larger committed watermarks without changing earlier bytes.</summary>
    public async Task<long> UploadAsync(string workerId, string assignmentId, int generation, string token, string sessionId,
        string segmentId, string kind, string? hash, long offset, long length, Stream content, CancellationToken cancellationToken)
    {
        ValidateArtifact(segmentId, kind, hash, length);
        var gate = Gate(sessionId);
        await gate.WaitAsync(cancellationToken);
        string? chunkPath = null;
        try
        {
            await using var ingestLock = await MediaJobExecutionLock.AcquireAsync(paths, "recording-ingest-" + sessionId, cancellationToken);
            var assignment = await ownership.RequireAsync(workerId, assignmentId, generation, token, cancellationToken);
            var session = await RequireSessionAsync(assignment, sessionId, cancellationToken);
            await RequireActiveLibraryAsync(session, cancellationToken);
            var settings = await settingsStore.GetAsync(cancellationToken);
            if (new DriveInfo(paths.RecordsDirectory).AvailableFreeSpace - MAX_CHUNK_BYTES < StorageService.MediaThreshold(settings))
            {
                throw new InfrastructureException("录像补传所需空间不足。", 507);
            }
            var chunks = Path.Combine(RecordingIngestPaths.Staging(paths, session), "chunks");
            Directory.CreateDirectory(chunks);
            chunkPath = Path.Combine(chunks, Guid.NewGuid().ToString("N") + ".tmp");
            await ReceiveChunkAsync(content, chunkPath, offset, length, cancellationToken);
            await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
            await ownership.RequireAsync(workerId, assignmentId, generation, token, cancellationToken);
            session = await RequireSessionAsync(assignment, sessionId, cancellationToken);
            await RequireActiveLibraryAsync(session, cancellationToken);
            if (kind is "media" or "index" or "prefix")
            {
                var segment = await RequireSegmentAsync(assignment, session, segmentId, cancellationToken, true);
                if (segment.Retired || segment.Commit is { IsClosed: true })
                {
                    throw new InfrastructureException("已封口或已回收录像只允许查询确认偏移。", 409);
                }
            }
            else if (session.Events is { IsClosed: true })
            {
                throw new InfrastructureException("已封口弹幕只允许查询确认偏移。", 409);
            }
            var path = RecordingIngestPaths.Artifact(paths, session, segmentId, kind, hash);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (kind is "prefix" or "event-index")
            {
                await RequireBlobLengthAsync(path, length, cancellationToken);
            }
            await using var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, 65536, true);
            if (offset < 0 || offset != output.Length || offset >= length)
            {
                throw new InfrastructureException("录像补传偏移已改变。", 409);
            }
            output.Position = offset;
            try
            {
                await using var chunk = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                await chunk.CopyToAsync(output, cancellationToken);
                await ownership.RequireAsync(workerId, assignmentId, generation, token, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(true);
                return output.Length;
            }
            catch
            {
                output.SetLength(offset);
                throw;
            }
        }
        finally
        {
            if (chunkPath is not null)
            {
                File.Delete(chunkPath);
            }
            gate.Release();
        }
    }

    private static async Task ReceiveChunkAsync(Stream content, string path, long offset, long length, CancellationToken cancellationToken)
    {
        if (offset < 0 || offset >= length)
        {
            throw new InfrastructureException("录像补传偏移无效。", 409);
        }
        await using var pending = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        var written = 0L;
        try
        {
            while (true)
            {
                var read = await content.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }
                written += read;
                if (written > MAX_CHUNK_BYTES || written > length - offset)
                {
                    throw new InfrastructureException("录像补传块超过允许边界。", 413);
                }
                await pending.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (written == 0)
            {
                throw new InfrastructureException("录像补传块不能为空。");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Applies the exact snapshot prefix after media and index boundaries are present, then advances published recording progress.</summary>
    public async Task<RecordingCommitAcknowledgement> CommitAsync(string workerId, string assignmentId, int generation, string token,
        RecordingSegmentCommit commit, CancellationToken cancellationToken)
    {
        RecordingIngestValidation.ValidateCommit(commit);
        var gate = Gate(commit.SessionId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var ingestLock = await MediaJobExecutionLock.AcquireAsync(paths, "recording-ingest-" + commit.SessionId, cancellationToken);
            await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
            var assignment = await ownership.RequireAsync(workerId, assignmentId, generation, token, cancellationToken, true);
            var session = await RequireSessionAsync(assignment, commit.SessionId, cancellationToken);
            var segment = await RequireSegmentAsync(assignment, session, commit.SegmentId, cancellationToken, assignment.State != "Released");
            if (segment.Commit is { } previous && commit.CommitSequence <= previous.CommitSequence)
            {
                if (commit.CommitSequence == previous.CommitSequence && JsonSerializer.Serialize(commit) != JsonSerializer.Serialize(previous))
                {
                    throw new InfrastructureException("同一录像提交序号内容不一致。", 409);
                }
                if (!segment.Retired && !await IsLibraryUnavailableAsync(session, cancellationToken))
                {
                    await PublishSegmentAsync(assignment, session, segment, cancellationToken);
                }
                return Acknowledge(previous);
            }
            if (assignment.State == "Released" || segment.Commit is { IsClosed: true }
                || segment.Commit is { } old && (commit.MediaCommittedBytes < old.MediaCommittedBytes
                    || commit.IndexCommittedBytes < old.IndexCommittedBytes || commit.DurationMs < old.DurationMs
                    || commit.StartOffsetMs != old.StartOffsetMs || commit.Codec != old.Codec || commit.PrefixLength != old.PrefixLength))
            {
                throw new InfrastructureException("录像提交不能回退或修改已封口片段。", 409);
            }
            await RequireActiveLibraryAsync(session, cancellationToken);
            if (segment.Retired)
            {
                throw new InfrastructureException("录像片段已移入回收站。", 410);
            }
            var media = RecordingIngestPaths.Segment(paths, session, segment.Id);
            var uploadedIndex = RecordingIngestPaths.Artifact(paths, session, segment.Id, "index", null);
            var prefix = RecordingIngestPaths.Artifact(paths, session, segment.Id, "prefix", commit.PrefixSha256.ToLowerInvariant());
            if (!File.Exists(media) || new FileInfo(media).Length < commit.MediaCommittedBytes
                || !File.Exists(prefix) || new FileInfo(prefix).Length != commit.PrefixLength || !File.Exists(uploadedIndex))
            {
                throw new InfrastructureException("录像提交引用的数据尚未收齐。", 409);
            }
            var prefixBytes = await File.ReadAllBytesAsync(prefix, cancellationToken);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(prefixBytes)), commit.PrefixSha256, StringComparison.OrdinalIgnoreCase)
                || !prefixBytes.AsSpan(0, 3).SequenceEqual("FLV"u8))
            {
                throw new InfrastructureException("录像前缀校验失败。", 422);
            }
            RecordingIngestValidation.ValidatePrefix(prefixBytes);
            await using (var source = new FileStream(media, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true))
            {
                var header = new byte[24];
                await source.ReadExactlyAsync(header, cancellationToken);
                if (!header.AsSpan().SequenceEqual(prefixBytes.AsSpan(0, header.Length)))
                {
                    throw new InfrastructureException("录像前缀修改了固定FLV或metadata标签头。", 422);
                }
            }
            await RecordingIngestValidation.ValidateIndexAsync(uploadedIndex, commit, cancellationToken);
            await ownership.RequireAsync(workerId, assignmentId, generation, token, cancellationToken);
            await using (var destination = new FileStream(media, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 65536, true))
            {
                await destination.WriteAsync(prefixBytes, cancellationToken);
                await destination.FlushAsync(cancellationToken);
                destination.Flush(true);
            }
            await PublishFilePrefixAsync(uploadedIndex, RecordingIngestPaths.SegmentIndex(paths, session, segment.Id),
                commit.IndexCommittedBytes, cancellationToken);
            segment.Commit = commit;
            await Segments.UpdateOneAsync(value => value.Id == segment.Id && value.AssignmentId == assignmentId,
                Builders<RecordingSegmentReceipt>.Update.Set(value => value.Commit, commit), cancellationToken: cancellationToken);
            await PublishSegmentAsync(assignment, session, segment, cancellationToken);
            return Acknowledge(commit);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Publishes complete event lines and atomically replaces the corresponding immutable index snapshot.</summary>
    public async Task<RecordingEventAcknowledgement> CommitEventsAsync(string workerId, string assignmentId, int generation, string token,
        string sessionId, RecordingEventCommit commit, CancellationToken cancellationToken)
    {
        RecordingIngestPaths.RequireHash(commit.IndexSha256);
        if (commit.CommittedBytes < 0 || commit.IndexCommittedBytes < 0 || commit.ThroughSequence < 0)
        {
            throw new InfrastructureException("弹幕提交水位无效。");
        }
        var gate = Gate(sessionId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var ingestLock = await MediaJobExecutionLock.AcquireAsync(paths, "recording-ingest-" + sessionId, cancellationToken);
            await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
            var assignment = await ownership.RequireAsync(workerId, assignmentId, generation, token, cancellationToken, true);
            var session = await RequireSessionAsync(assignment, sessionId, cancellationToken);
            if (session.Events == commit)
            {
                if (!await IsLibraryUnavailableAsync(session, cancellationToken))
                {
                    await PublishSessionAsync(assignment, session, cancellationToken);
                }
                return EventAcknowledge(commit);
            }
            if (assignment.State == "Released" || session.Events is { IsClosed: true }
                || session.Events is { } previous && (commit.CommittedBytes < previous.CommittedBytes || commit.ThroughSequence < previous.ThroughSequence))
            {
                throw new InfrastructureException("弹幕提交不能回退或修改已封口场次。", 409);
            }
            await RequireActiveLibraryAsync(session, cancellationToken);
            var input = RecordingIngestPaths.Artifact(paths, session, "", "events", null);
            var index = RecordingIngestPaths.Artifact(paths, session, "", "event-index", commit.IndexSha256.ToLowerInvariant());
            Directory.CreateDirectory(Path.GetDirectoryName(input)!);
            if (commit.CommittedBytes == 0 && !File.Exists(input))
            {
                await File.WriteAllBytesAsync(input, [], cancellationToken);
            }
            if (commit.IndexCommittedBytes == 0 && !File.Exists(index))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(index)!);
                await File.WriteAllBytesAsync(index, [], cancellationToken);
            }
            if (!File.Exists(input) || new FileInfo(input).Length < commit.CommittedBytes || !File.Exists(index)
                || new FileInfo(index).Length != commit.IndexCommittedBytes)
            {
                throw new InfrastructureException("弹幕数据与索引尚未收齐。", 409);
            }
            await using (var indexInput = File.OpenRead(index))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(indexInput, cancellationToken));
                if (!string.Equals(hash, commit.IndexSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InfrastructureException("弹幕索引校验失败。", 422);
                }
            }
            await ValidateEventsAsync(input, commit, session.Events, cancellationToken);
            await ownership.RequireAsync(workerId, assignmentId, generation, token, cancellationToken);
            var directory = MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(session.DirectoryPath, "弹幕"));
            await PublishFilePrefixAsync(input, Path.Combine(directory, "事件.jsonl"), commit.CommittedBytes, cancellationToken);
            await PublishFilePrefixAsync(index, Path.Combine(directory, "事件.index.jsonl"), commit.IndexCommittedBytes, cancellationToken, true);
            session.Events = commit;
            await Sessions.UpdateOneAsync(value => value.Id == session.Id && value.AssignmentId == assignmentId,
                Builders<RecordingSessionReceipt>.Update.Set(value => value.Events, commit), cancellationToken: cancellationToken);
            await PublishSessionAsync(assignment, session, cancellationToken);
            await CleanupFinalizedAsync(session, cancellationToken);
            return EventAcknowledge(commit);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Releases room ownership only after every locally declared session and its media and event receipts are sealed.</summary>
    public async Task StopAsync(string workerId, string assignmentId, int generation, string token,
        RecordingStopAcknowledgement acknowledgement, CancellationToken cancellationToken)
    {
        var assignment = await ownership.RequireAsync(workerId, assignmentId, generation, token, cancellationToken, true);
        if (assignment.State == "Released")
        {
            return;
        }
        var worker = await context.Collection<TranscodeWorker>("transcodeWorkers").Find(value => value.Id == workerId).FirstAsync(cancellationToken);
        var target = await context.Collection<Streamer>("streamers").Find(value => value.Id == assignment.TargetId).FirstOrDefaultAsync(cancellationToken);
        if (worker.Enabled && target is { Enabled: true, IsMonitored: true } && assignment.State == "Owned")
        {
            throw new InfrastructureException("仍在监控的房间不能提前释放归属。", 409);
        }
        var sessions = await Sessions.Find(value => value.AssignmentId == assignmentId).ToListAsync(cancellationToken);
        if (acknowledgement.PendingUploadBytes != 0 || sessions.Any(value => !acknowledgement.SessionIds.Contains(value.Id))
            || acknowledgement.SessionIds.Any(id => sessions.All(value => value.Id != id)))
        {
            throw new InfrastructureException("录像仍有未确认的补传数据。", 409);
        }
        foreach (var session in sessions)
        {
            if (!await IsSealedAsync(session, cancellationToken))
            {
                throw new InfrastructureException("录像场次尚未完成封口。", 409);
            }
        }
        await ownership.ReleaseWorkerAsync(assignment, cancellationToken);
        var staging = MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(".work", "recording", assignmentId));
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, true);
        }
        await context.Collection<Streamer>("streamers").UpdateOneAsync(value => value.Id == assignment.TargetId,
            Builders<Streamer>.Update.Set(value => value.State, target is { Enabled: true, IsMonitored: true } ? "Waiting" : "Stopped"), cancellationToken: cancellationToken);
        await publisher.PublishAsync("streamer", assignment.TargetId, cancellationToken: cancellationToken);
    }

    private IMongoCollection<RecordingSessionReceipt> Sessions => context.Collection<RecordingSessionReceipt>("recordingSessions");
    private IMongoCollection<RecordingSegmentReceipt> Segments => context.Collection<RecordingSegmentReceipt>("recordingSegments");

    private async Task<RecordingSessionReceipt> RequireSessionAsync(RecordingAssignmentRecord assignment, string sessionId, CancellationToken cancellationToken)
    {
        RecordingIngestPaths.RequireId(sessionId);
        var session = await Sessions.Find(value => value.Id == sessionId).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InfrastructureException("录像场次尚未注册。", 409);
        RequireSessionIdentity(session, assignment, sessionId);
        return session;
    }

    private async Task<RecordingSegmentReceipt> RequireSegmentAsync(RecordingAssignmentRecord assignment, RecordingSessionReceipt session,
        string segmentId, CancellationToken cancellationToken, bool create)
    {
        RecordingIngestPaths.RequireId(segmentId);
        var segment = await Segments.Find(value => value.Id == segmentId).FirstOrDefaultAsync(cancellationToken);
        if (segment is null && create)
        {
            await RequireActiveLibraryAsync(session, cancellationToken);
            if (session.Descriptor.IsClosed)
            {
                throw new InfrastructureException("已封口场次不接受新的录像片段。", 409);
            }
            if (await context.Collection<RecordingSegment>("segments").Find(value => value.Id == segmentId).AnyAsync(cancellationToken))
            {
                throw new InfrastructureException("录像片段标识已被使用。", 409);
            }
            segment = new() { Id = segmentId, AssignmentId = assignment.Id, Generation = assignment.Generation, SessionId = session.Id };
            await Segments.InsertOneAsync(segment, cancellationToken: cancellationToken);
        }
        if (segment is null || segment.AssignmentId != assignment.Id || segment.Generation != assignment.Generation || segment.SessionId != session.Id)
        {
            throw new InfrastructureException("录像片段归属不符。", 409);
        }
        return segment;
    }

    private async Task PublishSegmentAsync(RecordingAssignmentRecord assignment, RecordingSessionReceipt session,
        RecordingSegmentReceipt segment, CancellationToken cancellationToken)
    {
        var commit = segment.Commit!;
        if (commit.DurationMs > 0)
        {
            var librarySegment = new RecordingSegment
            {
                Id = segment.Id, AssetId = segment.Id, AttemptId = assignment.Id, SessionId = session.Id,
                RelativePath = RecordingIngestPaths.SegmentRelative(segment.Id), IndexRelativePath = RecordingIngestPaths.SegmentIndexRelative(segment.Id),
                StartOffsetMs = commit.StartOffsetMs, DurationMs = commit.DurationMs, CommittedBytes = commit.MediaCommittedBytes,
                Codec = commit.Codec, IsClosed = commit.IsClosed
            };
            await context.Collection<RecordingSegment>("segments").ReplaceOneAsync(value => value.Id == segment.Id,
                librarySegment, new ReplaceOptions { IsUpsert = true }, cancellationToken);
            session.Published = true;
            await Sessions.UpdateOneAsync(value => value.Id == session.Id,
                Builders<RecordingSessionReceipt>.Update.Set(value => value.Published, true), cancellationToken: cancellationToken);
        }
        await PublishSessionAsync(assignment, session, cancellationToken);
        await CleanupFinalizedAsync(session, cancellationToken);
    }

    private async Task PublishSessionAsync(RecordingAssignmentRecord assignment, RecordingSessionReceipt receipt, CancellationToken cancellationToken)
    {
        if (!receipt.Published)
        {
            return;
        }
        await RequireActiveLibraryAsync(receipt, cancellationToken);
        var descriptor = receipt.Descriptor;
        var segments = await context.Collection<RecordingSegment>("segments").Find(value => value.SessionId == receipt.Id).ToListAsync(cancellationToken);
        var existing = await context.Collection<BroadcastSession>("sessions").Find(value => value.Id == receipt.Id).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null && existing.RecordingAssignmentId != assignment.Id)
        {
            throw new InfrastructureException("录像场次已归属其它采集端。", 409);
        }
        var session = existing ?? new BroadcastSession
        {
            Id = receipt.Id, StreamerId = assignment.TargetId, RoomId = assignment.RoomId, Title = descriptor.Title,
            CoverUrl = descriptor.CoverUrl, LiveTime = descriptor.LiveTime, StartedAtUtc = descriptor.StartedAtUtc,
            DirectoryPath = receipt.DirectoryPath, HasDanmu = descriptor.HasDanmu, AutoTranscodeEligible = true,
            RecordingAssignmentId = assignment.Id, RecordingGeneration = assignment.Generation
        };
        var duration = segments.Select(value => value.StartOffsetMs + value.DurationMs).DefaultIfEmpty(0).Max();
        session.LastMediaAtUtc = session.StartedAtUtc.AddMilliseconds(duration);
        session.TotalBytes = segments.Sum(value => value.CommittedBytes);
        session.HasDanmu |= descriptor.HasDanmu;
        session.State = await IsSealedAsync(receipt, cancellationToken) ? "Completed" : "Recording";
        session.EndedAtUtc = session.State == "Completed"
            ? descriptor.EndedAtUtc > session.LastMediaAtUtc ? descriptor.EndedAtUtc : session.LastMediaAtUtc : null;
        await SessionManifestFiles.WriteAsync(Path.Combine(MediaPaths.Resolve(paths.RecordsDirectory, receipt.DirectoryPath), "场次.json"),
            new() { Session = session, Segments = segments }, cancellationToken);
        if (existing is null)
        {
            await context.Collection<BroadcastSession>("sessions").InsertOneAsync(session, cancellationToken: cancellationToken);
        }
        else
        {
            await context.Collection<BroadcastSession>("sessions").UpdateOneAsync(value => value.Id == session.Id && value.RecordingAssignmentId == assignment.Id,
                Builders<BroadcastSession>.Update.Set(value => value.LastMediaAtUtc, session.LastMediaAtUtc).Set(value => value.TotalBytes, session.TotalBytes)
                    .Set(value => value.State, session.State).Set(value => value.EndedAtUtc, session.EndedAtUtc)
                    .Set(value => value.HasDanmu, session.HasDanmu), cancellationToken: cancellationToken);
        }
        receipt.LibraryPublished = true;
        await Sessions.UpdateOneAsync(value => value.Id == receipt.Id,
            Builders<RecordingSessionReceipt>.Update.Set(value => value.LibraryPublished, true), cancellationToken: cancellationToken);
        await publisher.PublishAsync("session", session.Id, cancellationToken: cancellationToken);
    }

    private async Task<bool> IsSealedAsync(RecordingSessionReceipt session, CancellationToken cancellationToken)
    {
        if (!session.Descriptor.IsClosed || session.Descriptor.HasDanmu && session.Events is not { IsClosed: true })
        {
            return false;
        }
        var segments = await Segments.Find(value => value.SessionId == session.Id && value.AssignmentId == session.AssignmentId).ToListAsync(cancellationToken);
        return segments.All(value => value.Commit is { IsClosed: true });
    }

    private async Task<bool> IsLibraryUnavailableAsync(RecordingSessionReceipt receipt, CancellationToken cancellationToken)
    {
        if (receipt.Retired)
        {
            return true;
        }
        var session = await context.Collection<BroadcastSession>("sessions").Find(value => value.Id == receipt.Id).FirstOrDefaultAsync(cancellationToken);
        return session is null ? receipt.LibraryPublished
            : session.IsTrashed || session.IsHidden || !session.ProjectionReady || session.RecordingAssignmentId != receipt.AssignmentId || session.MediaRevision != 1;
    }

    private async Task RequireActiveLibraryAsync(RecordingSessionReceipt receipt, CancellationToken cancellationToken)
    {
        if (await IsLibraryUnavailableAsync(receipt, cancellationToken))
        {
            throw new InfrastructureException("录像已被移除、回收或重新投影。", 410);
        }
    }

    private async Task CleanupFinalizedAsync(RecordingSessionReceipt receipt, CancellationToken cancellationToken)
    {
        if (!await IsSealedAsync(receipt, cancellationToken) || receipt.Published && !receipt.LibraryPublished)
        {
            return;
        }
        var staging = RecordingIngestPaths.Staging(paths, receipt);
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, true);
        }
        if (!receipt.Published)
        {
            var directory = MediaPaths.Resolve(paths.RecordsDirectory, receipt.DirectoryPath);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static async Task PublishFilePrefixAsync(string sourcePath, string destinationPath, long length,
        CancellationToken cancellationToken, bool replace = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var target = replace ? destinationPath + ".publishing-" + Guid.NewGuid().ToString("N") : destinationPath;
        try
        {
            await using (var output = new FileStream(target, replace ? FileMode.CreateNew : FileMode.OpenOrCreate, FileAccess.Write,
                FileShare.ReadWrite, 65536, true))
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, true))
            {
                if (output.Length > length)
                {
                    throw new InfrastructureException("归档提交水位不能缩短。", 409);
                }
                input.Position = output.Length;
                output.Position = output.Length;
                var remaining = length - output.Length;
                var buffer = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    while (remaining > 0)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                        if (read == 0)
                        {
                            throw new EndOfStreamException("The recording upload ended before its committed boundary.");
                        }
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        remaining -= read;
                    }
                    await output.FlushAsync(cancellationToken);
                    output.Flush(true);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            if (replace)
            {
                File.Move(target, destinationPath, true);
            }
        }
        finally
        {
            if (replace)
            {
                File.Delete(target);
            }
        }
    }

    private static async Task ValidateEventsAsync(string path, RecordingEventCommit commit, RecordingEventCommit? previous, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, true);
        stream.Position = previous?.CommittedBytes ?? 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 65536, true);
        var bytes = previous?.CommittedBytes ?? 0;
        var sequence = previous?.ThroughSequence ?? 0;
        while (bytes < commit.CommittedBytes)
        {
            var line = await reader.ReadLineAsync(cancellationToken) ?? throw new InfrastructureException("弹幕行尚未完整上传。", 409);
            bytes += Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes > commit.CommittedBytes)
            {
                throw new InfrastructureException("弹幕提交不能结束于半行。", 422);
            }
            using var document = JsonDocument.Parse(line);
            var next = document.RootElement.GetProperty("sequence").GetInt64();
            if (next <= sequence)
            {
                throw new InfrastructureException("弹幕序号不能重复或回退。", 422);
            }
            sequence = next;
        }
        if (bytes != commit.CommittedBytes || sequence != commit.ThroughSequence)
        {
            throw new InfrastructureException("弹幕提交序号与内容不符。", 422);
        }
    }

    private static async Task RequireBlobLengthAsync(string path, long length, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var marker = path + ".length";
        var expected = length.ToString(CultureInfo.InvariantCulture);
        if (File.Exists(marker))
        {
            if (await File.ReadAllTextAsync(marker, cancellationToken) != expected)
            {
                throw new InfrastructureException("不可变录像blob长度已改变。", 409);
            }
        }
        else
        {
            await PrivateFiles.WriteAllTextAsync(marker, expected, cancellationToken);
        }
    }

    private static void RequireSessionIdentity(RecordingSessionReceipt session, RecordingAssignmentRecord assignment, string sessionId)
    {
        if (session.Id != sessionId || session.AssignmentId != assignment.Id || session.Generation != assignment.Generation)
        {
            throw new InfrastructureException("录像场次不属于此执行代次。", 409);
        }
    }

    private static void ValidateArtifact(string segmentId, string kind, string? hash, long length)
    {
        if (length < 0 || kind is not ("media" or "index" or "prefix" or "events" or "event-index"))
        {
            throw new InfrastructureException("录像上传类型或长度无效。");
        }
        if (kind is "media" or "index" or "prefix")
        {
            RecordingIngestPaths.RequireId(segmentId);
        }
        if (kind is "prefix" or "event-index")
        {
            RecordingIngestPaths.RequireHash(hash ?? "");
        }
        if (kind == "prefix" && length > 32 * 1024 * 1024)
        {
            throw new InfrastructureException("录像前缀超过FLV元数据大小边界。", 413);
        }
    }

    private SemaphoreSlim Gate(string id) => gates[(int)(Convert.ToUInt32(TranscodeAssetRegistry.Hash(id)[..8], 16) % (uint)gates.Length)];
    private static RecordingCommitAcknowledgement Acknowledge(RecordingSegmentCommit commit) => new()
    {
        CommitSequence = commit.CommitSequence, MediaCommittedBytes = commit.MediaCommittedBytes,
        IndexCommittedBytes = commit.IndexCommittedBytes, IsClosed = commit.IsClosed
    };
    private static RecordingEventAcknowledgement EventAcknowledge(RecordingEventCommit commit) => new()
    {
        CommittedBytes = commit.CommittedBytes, IndexCommittedBytes = commit.IndexCommittedBytes,
        ThroughSequence = commit.ThroughSequence, IsClosed = commit.IsClosed
    };

    /// <summary>Releases bounded transport gates after request processing stops.</summary>
    public void Dispose()
    {
        foreach (var gate in gates)
        {
            gate.Dispose();
        }
    }
}
