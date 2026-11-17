using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Storage;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Clipping;

/// <summary>Transfers bounded clipping inputs and publishes verified MP4 outputs through the existing media library.</summary>
public sealed class ClipTransferService(IMongoContext context, ClipLeaseService leases, ClipInputPlanner inputs, IAppPaths paths,
    ISettingsStore settingsStore, IMediaProcessor processor, ClipPublicationService publication, IStatusPublisher publisher) : IDisposable
{
    private const int MAX_CHUNK_BYTES = 8 * 1024 * 1024;
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>Reauthorizes a single planned input without exposing unrelated bytes from its source file.</summary>
    public async Task<ClipInput> GetInputAsync(string workerId, string jobId, int attempt, string token, string inputId,
        CancellationToken cancellationToken = default)
    {
        var job = await leases.RequireAsync(workerId, jobId, attempt, token, cancellationToken);
        var input = job.RemoteAssignment?.Inputs.FirstOrDefault(value => value.Id == inputId && !value.IsGap)
            ?? throw new InfrastructureException("切片输入不存在。", 404);
        var source = job.RemoteSources.Single(value => value.Id == inputId);
        var range = input.Range!;
        return new(MediaPaths.Resolve(paths.RecordsDirectory, source.RelativePath), input.SourceETag,
            range.SourceLength, range.StartOffset, range.EndOffset);
    }

    /// <summary>Queries the committed output offset for the current attempt and immutable total length.</summary>
    public async Task<long> GetUploadOffsetAsync(string workerId, string jobId, int attempt, string token, long length,
        CancellationToken cancellationToken = default)
    {
        var gate = Gate(jobId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var job = await leases.RequireAsync(workerId, jobId, attempt, token, cancellationToken);
            var output = await UploadPathAsync(job, length, cancellationToken);
            return File.Exists(output) ? new FileInfo(output).Length : 0;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Appends a bounded upload chunk and rolls back that chunk if its lease or content becomes invalid.</summary>
    public async Task<long> UploadAsync(string workerId, string jobId, int attempt, string token, long offset, long length,
        Stream content, CancellationToken cancellationToken = default)
    {
        var gate = Gate(jobId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var job = await leases.RequireAsync(workerId, jobId, attempt, token, cancellationToken);
            var settings = await settingsStore.GetAsync(cancellationToken);
            if (new DriveInfo(paths.RecordsDirectory).AvailableFreeSpace - MAX_CHUNK_BYTES <= StorageService.MediaThreshold(settings))
            {
                throw new InfrastructureException("切片上传所需存储空间不足。", 507);
            }
            var path = await UploadPathAsync(job, length, cancellationToken);
            await using var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous);
            if (offset < 0 || offset != output.Length || offset >= length)
            {
                throw new InfrastructureException("上传偏移已改变，请重新查询断点。", 409);
            }
            output.Position = offset;
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
                        throw new InfrastructureException("上传块超过允许长度。", 413);
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (written == 0)
                {
                    throw new InfrastructureException("上传块不能为空。");
                }
                await leases.RequireAsync(workerId, jobId, attempt, token, cancellationToken);
                await output.FlushAsync(cancellationToken);
                return output.Length;
            }
            catch
            {
                output.SetLength(offset);
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Verifies byte identity, playable media, planned bounds and the current lease before publishing a clip.</summary>
    public async Task CompleteAsync(string workerId, string jobId, int attempt, string token, ClipOutputManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ValidateManifest(manifest);
        var gate = Gate(jobId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await leases.RequireWorkerAsync(workerId, cancellationToken);
            if (await IsCompletedAsync(workerId, jobId, attempt, token, manifest, cancellationToken))
            {
                return;
            }
            var job = await leases.RequireAsync(workerId, jobId, attempt, token, cancellationToken);
            var assignment = job.RemoteAssignment ?? throw new InfrastructureException("切片计划不存在。", 409);
            if (Math.Abs(manifest.ActualStartMs - assignment.ActualStartMs) > 0.1
                || Math.Abs(manifest.ActualEndMs - assignment.ActualEndMs) > 0.1)
            {
                throw new InfrastructureException("切片成品的时间范围与任务计划不符。", 422);
            }
            var session = await context.Collection<BroadcastSession>("sessions").Find(value => value.Id == job.SessionId)
                .FirstAsync(cancellationToken);
            var directory = MediaJobFiles.OutputDirectory(job, session, paths.RecordsDirectory, job.Attempt);
            var destination = Path.Combine(directory, MediaPaths.SafeName(job.Title, 40) + ".mp4");
            var source = File.Exists(destination) ? destination : await UploadPathAsync(job, manifest.Bytes, cancellationToken);
            var file = new FileInfo(source);
            if (!file.Exists || file.Length != manifest.Bytes)
            {
                throw new InfrastructureException("切片成品尚未完整上传。", 409);
            }
            await using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                if (!string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InfrastructureException("切片成品内容校验失败。", 422);
                }
            }
            var probe = await processor.ProbeAsync(source, cancellationToken);
            var expected = assignment.ActualEndMs - assignment.ActualStartMs;
            var tolerance = job.Mode == "Exact" ? Math.Max(150, probe.FrameRate > 0 ? 1000 / probe.FrameRate + 75 : 150) : 500;
            if (!double.IsFinite(probe.DurationMs) || probe.DurationMs <= 0 || probe.Width <= 0 || probe.Height <= 0
                || string.IsNullOrWhiteSpace(probe.VideoCodec) || Math.Abs(probe.DurationMs - expected) > tolerance)
            {
                throw new InfrastructureException("切片成品的媒体信息或时长无效。", 422);
            }
            await inputs.ValidateContentAsync(job, cancellationToken);
            await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, jobId, cancellationToken);
            await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
            await leases.RequireAsync(workerId, jobId, attempt, token, cancellationToken);
            var changed = await Jobs.UpdateOneAsync(ClipLeaseService.Fence(workerId, jobId, attempt, token),
                Builders<MediaJob>.Update.Set(value => value.State, "Publishing").Set(value => value.RemoteOutputSha256, manifest.Sha256.ToLowerInvariant()),
                cancellationToken: cancellationToken);
            if (changed.MatchedCount == 0)
            {
                throw new InfrastructureException("切片发布租约已失效。", 409);
            }
            Directory.CreateDirectory(directory);
            if (source != destination)
            {
                File.Move(source, destination, false);
            }
            var relative = Path.GetRelativePath(paths.RecordsDirectory, destination);
            var output = JobOutputManifest.Create(job);
            output.Paths.Add(relative);
            output.ArtifactLengths.Add(relative, manifest.Bytes);
            output.ActualStartMs = assignment.ActualStartMs;
            output.ActualEndMs = assignment.ActualEndMs;
            await JsonFiles.WriteAsync(Path.Combine(Staging(job), "completed.json"), output, cancellationToken);
            var settings = await settingsStore.GetAsync(cancellationToken);
            await publication.PublishAsync(job, session, output, settings,
                ct => leases.RequireAsync(workerId, jobId, attempt, token, ct), cancellationToken);
            changed = await Jobs.UpdateOneAsync(ClipLeaseService.Fence(workerId, jobId, attempt, token),
                Builders<MediaJob>.Update.Set(value => value.State, "Completed").Set(value => value.Progress, 100)
                    .Set(value => value.OutputRelativePath, relative).Set(value => value.OutputRelativePaths, output.Paths)
                    .Set(value => value.Error, null).Set(value => value.ErrorCode, null).Set(value => value.ErrorArguments, [])
                    .Set(value => value.FinishedAtUtc, DateTime.UtcNow).Set(value => value.LeaseOwner, null).Set(value => value.LeaseUntilUtc, null),
                cancellationToken: cancellationToken);
            if (changed.MatchedCount == 0)
            {
                throw new InfrastructureException("切片发布租约已失效。", 409);
            }
            await publication.ActivateAsync(job, cancellationToken);
            await publisher.PublishAsync("job", job.Id, job.OwnerId, cancellationToken);
            Cleanup(job);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Reports a failed or returned execution and invalidates its previous lease.</summary>
    public async Task FinishAsync(string workerId, string jobId, int attempt, string token, WorkerJobResult result,
        CancellationToken cancellationToken = default)
    {
        if (result.State != "Failed" && !(result.State == "Queued" && result.ErrorCode == "WorkerCapacityUnavailable")
            || result.Error?.Length > 4096 || result.ErrorCode?.Length > 128)
        {
            throw new InfrastructureException("切片任务终态无效。");
        }
        var gate = Gate(jobId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, jobId, cancellationToken);
            var job = await leases.RequireAsync(workerId, jobId, attempt, token, cancellationToken);
            var changed = await Jobs.UpdateOneAsync(ClipLeaseService.Fence(workerId, jobId, attempt, token),
                Builders<MediaJob>.Update.Set(value => value.State, result.State)
                    .Set(value => value.Error, result.State == "Failed" ? result.Error : null)
                    .Set(value => value.ErrorCode, result.State == "Failed" ? result.ErrorCode : null)
                    .Set(value => value.LeaseOwner, null).Set(value => value.LeaseUntilUtc, null).Set(value => value.RemoteLeaseTokenHash, null)
                    .Set(value => value.FinishedAtUtc, result.State == "Failed" ? DateTime.UtcNow : null),
                cancellationToken: cancellationToken);
            if (changed.MatchedCount > 0)
            {
                await publisher.PublishAsync("job", job.Id, job.OwnerId, cancellationToken);
                Cleanup(job);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private IMongoCollection<MediaJob> Jobs => context.Collection<MediaJob>("jobs");

    private async Task<bool> IsCompletedAsync(string workerId, string jobId, int attempt, string token, ClipOutputManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, jobId, cancellationToken);
        var hash = TranscodeWorkerService.SecretHash(token);
        var job = await Jobs.Find(value => value.Id == jobId && value.Kind == "Clip" && value.State == "Completed"
            && value.RemoteWorkerId == workerId && value.Attempt == attempt && value.RemoteLeaseTokenHash == hash && !value.CancelRequested)
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            return false;
        }
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var assignment = job.RemoteAssignment;
        var file = job.OutputRelativePath is { } relative ? new FileInfo(MediaPaths.Resolve(paths.RecordsDirectory, relative)) : null;
        if (file is null || !file.Exists || file.Length != manifest.Bytes || assignment is null
            || Math.Abs(assignment.ActualStartMs - manifest.ActualStartMs) > 0.1
            || Math.Abs(assignment.ActualEndMs - manifest.ActualEndMs) > 0.1
            || !string.Equals(job.RemoteOutputSha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InfrastructureException("切片完成确认与已发布成品不符。", 409);
        }
        await publication.ActivateAsync(job, cancellationToken);
        Cleanup(job);
        return true;
    }

    private async Task<string> UploadPathAsync(MediaJob job, long length, CancellationToken cancellationToken)
    {
        if (length <= 0)
        {
            throw new InfrastructureException("切片上传长度无效。");
        }
        var directory = Staging(job);
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "output.length");
        var value = length.ToString(CultureInfo.InvariantCulture);
        if (File.Exists(marker))
        {
            if (await File.ReadAllTextAsync(marker, cancellationToken) != value)
            {
                throw new InfrastructureException("切片成品总长度已改变。", 409);
            }
        }
        else
        {
            await PrivateFiles.WriteAllTextAsync(marker, value, cancellationToken);
        }
        return Path.Combine(directory, "output.mp4");
    }

    private string Staging(MediaJob job) => MediaPaths.Resolve(paths.RecordsDirectory,
        Path.Combine(".work", "jobs", job.Id, job.Attempt.ToString(CultureInfo.InvariantCulture)));

    private void Cleanup(MediaJob job)
    {
        var directory = Staging(job);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private SemaphoreSlim Gate(string jobId) => gates[(int)(Convert.ToUInt32(TranscodeAssetRegistry.Hash(jobId)[..8], 16) % (uint)gates.Length)];

    private static void ValidateManifest(ClipOutputManifest manifest)
    {
        if (manifest.Bytes <= 0 || manifest.Sha256 is not { Length: 64 } || !manifest.Sha256.All(char.IsAsciiHexDigit)
            || !double.IsFinite(manifest.ActualStartMs) || manifest.ActualStartMs < 0
            || !double.IsFinite(manifest.ActualEndMs) || manifest.ActualEndMs <= manifest.ActualStartMs)
        {
            throw new InfrastructureException("切片完成清单无效。");
        }
    }

    /// <summary>Releases upload serialization gates when the application shuts down.</summary>
    public void Dispose()
    {
        foreach (var gate in gates)
        {
            gate.Dispose();
        }
    }
}
