using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;

namespace Yohuke.LiveRecorder.Infrastructure.Clipping;

/// <summary>Builds bounded remote inputs from the job's immutable committed recording snapshot.</summary>
public sealed class ClipInputPlanner(IMongoContext context, IAppPaths paths, IIndexedFlvRangePlanner ranges)
{
    /// <summary>Returns no remote plan when a legacy source cannot provide indexed partial downloads.</summary>
    public async Task<ClipJobPlan?> TryPlanAsync(MediaJob job, CancellationToken cancellationToken, bool hashInputs = true)
    {
        try
        {
            return await PlanAsync(job, cancellationToken, hashInputs);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException
            || error is InfrastructureException { StatusCode: 404 or 409 or 410 })
        {
            return null;
        }
    }

    /// <summary>Resolves the requested session spans and decoder configurations without downloading whole recordings.</summary>
    public async Task<ClipJobPlan> PlanAsync(MediaJob job, CancellationToken cancellationToken, bool hashInputs = true)
    {
        if (job.Kind != "Clip" || job.SourceSegments.Count == 0)
        {
            throw new InvalidOperationException("Only indexed clipping jobs can be assigned remotely.");
        }
        var session = await RequireSessionAsync(job, cancellationToken);
        var inputs = new List<ClipInputSpan>();
        var sources = new List<ClipSourceSnapshot>();
        var cursor = job.StartMs;
        var fingerprint = JobOutputManifest.Create(job).InputFingerprint;
        foreach (var segment in job.SourceSegments.OrderBy(value => value.StartOffsetMs))
        {
            var from = Math.Max(cursor, segment.StartOffsetMs);
            var until = Math.Min(job.EndMs, segment.StartOffsetMs + segment.DurationMs);
            if (until <= from)
            {
                continue;
            }
            if (from > cursor)
            {
                inputs.Add(new() { Id = inputs.Count.ToString(), IsGap = true, SessionStartMs = cursor, DurationMs = from - cursor });
            }
            var source = MediaPaths.ResolveSegment(paths.RecordsDirectory, session.DirectoryPath, segment);
            var index = MediaPaths.ResolveSegmentIndex(paths.RecordsDirectory, session.DirectoryPath, segment);
            if (string.IsNullOrEmpty(index))
            {
                index = Path.ChangeExtension(source, ".index.jsonl");
            }
            var file = new FileInfo(source);
            if (!file.Exists || segment.CommittedBytes <= 0 || file.Length < segment.CommittedBytes)
            {
                throw new InvalidOperationException("The committed clipping source is unavailable.");
            }
            var range = await ranges.PlanAsync(index, segment.CommittedBytes, from - segment.StartOffsetMs, until - from, cancellationToken);
            var id = inputs.Count.ToString();
            var snapshot = new ClipSourceSnapshot
            {
                Id = id, SegmentId = segment.Id, RelativePath = Path.GetRelativePath(paths.RecordsDirectory, source),
                Length = file.Length, CreationTicks = file.CreationTimeUtc.Ticks,
                WriteTicks = file.LastWriteTimeUtc.Ticks, IsClosed = segment.IsClosed
            };
            var contentHash = hashInputs
                ? await ClipRangeContentHasher.HashAsync(source, range.StartOffset, range.EndOffset, cancellationToken)
                : new string('0', 64);
            var version = TranscodeAssetRegistry.Hash(FormattableString.Invariant(
                $"{fingerprint}:{snapshot.RelativePath}:{snapshot.CreationTicks}:{(snapshot.IsClosed ? snapshot.WriteTicks : 0)}:{segment.CommittedBytes}:{contentHash}"));
            inputs.Add(new()
            {
                Id = id, SessionStartMs = from, SourceStartMs = from - segment.StartOffsetMs, DurationMs = until - from,
                SourceFileName = Path.GetFileName(source), SourceVersion = version, SourceETag = "\"" + version + "\"",
                RangeSha256 = contentHash,
                Range = new()
                {
                    SourceLength = range.SourceLength, StartOffset = range.StartOffset, EndOffset = range.EndOffset,
                    StartMs = range.StartMs, EndMs = range.EndMs, VideoHeader = range.VideoHeader, AudioHeader = range.AudioHeader
                }
            });
            sources.Add(snapshot);
            cursor = until;
            if (cursor >= job.EndMs)
            {
                break;
            }
        }
        if (cursor < job.EndMs)
        {
            inputs.Add(new() { Id = inputs.Count.ToString(), IsGap = true, SessionStartMs = cursor, DurationMs = job.EndMs - cursor });
        }
        if (sources.Count == 0 || job.Mode == "Fast" && inputs.Any(value => value.IsGap))
        {
            throw new InvalidOperationException("The clipping range cannot be processed remotely.");
        }
        if (job.Mode == "Fast")
        {
            var first = inputs[0];
            var start = first.Range!.StartMs;
            inputs[0] = first with
            {
                SessionStartMs = first.SessionStartMs + start - first.SourceStartMs,
                SourceStartMs = start, DurationMs = first.DurationMs + first.SourceStartMs - start
            };
            var last = inputs[^1];
            inputs[^1] = last with { DurationMs = last.Range!.EndMs - last.SourceStartMs };
        }
        return new(new()
        {
            JobId = job.Id, Mode = job.Mode, StartMs = job.StartMs, EndMs = job.EndMs,
            ActualStartMs = inputs[0].SessionStartMs,
            ActualEndMs = inputs[^1].SessionStartMs + inputs[^1].DurationMs, Inputs = inputs
        }, sources);
    }

    /// <summary>Rejects source replacement while allowing an active recording to append beyond the fixed snapshot.</summary>
    public async Task ValidateSourcesAsync(MediaJob job, CancellationToken cancellationToken)
    {
        await RequireSessionAsync(job, cancellationToken);
        foreach (var snapshot in job.RemoteSources)
        {
            var input = job.RemoteAssignment?.Inputs.FirstOrDefault(value => value.Id == snapshot.Id);
            var range = input?.Range ?? throw new InfrastructureException("切片输入计划已失效。", 410);
            var file = new FileInfo(MediaPaths.Resolve(paths.RecordsDirectory, snapshot.RelativePath));
            if (!file.Exists || file.CreationTimeUtc.Ticks != snapshot.CreationTicks || file.Length < range.SourceLength
                || snapshot.IsClosed && (file.Length != snapshot.Length || file.LastWriteTimeUtc.Ticks != snapshot.WriteTicks))
            {
                throw new InfrastructureException("切片源文件已被移除或替换。", 410);
            }
        }
    }

    /// <summary>Rechecks only the assigned range contents before accepting a completed output.</summary>
    public async Task ValidateContentAsync(MediaJob job, CancellationToken cancellationToken)
    {
        await ValidateSourcesAsync(job, cancellationToken);
        foreach (var snapshot in job.RemoteSources)
        {
            var input = job.RemoteAssignment!.Inputs.Single(value => value.Id == snapshot.Id);
            var range = input.Range!;
            var hash = await ClipRangeContentHasher.HashAsync(MediaPaths.Resolve(paths.RecordsDirectory, snapshot.RelativePath),
                range.StartOffset, range.EndOffset, cancellationToken);
            if (!string.Equals(hash, input.RangeSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InfrastructureException("切片已提交源内容发生变化。", 410);
            }
        }
        await ValidateSourcesAsync(job, cancellationToken);
    }

    private async Task<BroadcastSession> RequireSessionAsync(MediaJob job, CancellationToken cancellationToken)
    {
        var session = await context.Collection<BroadcastSession>("sessions")
            .Find(value => value.Id == job.SessionId && !value.IsTrashed && !value.IsHidden && value.ProjectionReady)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InfrastructureException("切片源场次不可用。", 410);
        var ids = job.SourceSegments.Select(value => value.Id).ToArray();
        if (session.MediaRevision != job.SourceRevision || await context.Collection<RecordingSegment>("segments")
                .CountDocumentsAsync(value => ids.Contains(value.Id) && value.SessionId == session.Id && value.Revision == job.SourceRevision,
                    cancellationToken: cancellationToken) != ids.Distinct().Count())
        {
            throw new InfrastructureException("切片源媒体版本已改变。", 410);
        }
        return session;
    }
}
