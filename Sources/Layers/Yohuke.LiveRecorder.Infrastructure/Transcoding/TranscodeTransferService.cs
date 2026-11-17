using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Storage;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>实现租约约束的断点传输与主站验证后的原子发布。</summary>
public sealed class TranscodeTransferService(IMongoContext context, TranscodeLeaseService leases,
    TranscodeAssetRegistry assets, IAppPaths paths, ISettingsStore settingsStore, ITranscodeMediaInspector inspector) : IDisposable
{
    private const int MAX_CHUNK_BYTES = 8 * 1024 * 1024;

    /// <summary>将续传断点绑定到成品指纹，重建产物只重置尚未发布的本代次暂存。</summary>
    public async Task BeginUploadAsync(string workerId, string jobId, int attempt, string leaseToken,
        WorkerUploadSession session, CancellationToken cancellationToken)
    {
        if (session.ManifestFingerprint is not { Length: 64 } || !session.ManifestFingerprint.All(char.IsAsciiHexDigit))
        {
            throw new InfrastructureException("上传成品指纹无效。");
        }
        var gate = Gate(jobId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, "transcoding-" + jobId, cancellationToken);
            var job = await leases.RequireAsync(workerId, jobId, attempt, leaseToken, cancellationToken);
            var directory = Staging(job);
            var marker = Path.Combine(directory, "upload-session.json");
            var fingerprint = session.ManifestFingerprint.ToLowerInvariant();
            if (File.Exists(marker) && await File.ReadAllTextAsync(marker, cancellationToken) == fingerprint)
            {
                return;
            }
            if (job.State == "Publishing" || job.PublicationDirectory is not null)
            {
                throw new InfrastructureException("已开始发布的成品不能重置上传。", 409);
            }
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
            Directory.CreateDirectory(directory);
            await PrivateFiles.WriteAllTextAsync(marker, fingerprint, cancellationToken);
            await leases.RequireAsync(workerId, jobId, attempt, leaseToken, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>在每次范围下载前验证当前执行权和已提交输入版本。</summary>
    public async Task<TranscodeInput> GetInputAsync(string workerId, string jobId, int attempt, string leaseToken,
        CancellationToken cancellationToken)
    {
        var job = await leases.RequireAsync(workerId, jobId, attempt, leaseToken, cancellationToken);
        var asset = await assets.RequireActiveAsync(job.AssetId, job.SourceVersion, cancellationToken);
        return new(assets.SourcePath(asset), "\"" + asset.SourceVersion + "\"", asset.CommittedBytes);
    }

    /// <summary>获取某一文件在当前执行代次已完整提交的偏移。</summary>
    public async Task<long> GetUploadOffsetAsync(string workerId, string jobId, int attempt, string leaseToken,
        string fileName, long length, CancellationToken cancellationToken)
    {
        var job = await leases.RequireAsync(workerId, jobId, attempt, leaseToken, cancellationToken);
        ValidateUpload(fileName, length, job.Profile);
        var gate = Gate(jobId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, "transcoding-" + jobId, cancellationToken);
            job = await leases.RequireAsync(workerId, jobId, attempt, leaseToken, cancellationToken);
            var path = await UploadPathAsync(job, fileName, length, cancellationToken);
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>在精确偏移写入一个有界块，失败时回滚当前块而保留之前的断点。</summary>
    public async Task<long> UploadAsync(string workerId, string jobId, int attempt, string leaseToken,
        string fileName, long offset, long length, Stream content, CancellationToken cancellationToken)
    {
        var job = await leases.RequireAsync(workerId, jobId, attempt, leaseToken, cancellationToken);
        ValidateUpload(fileName, length, job.Profile);
        var settings = await settingsStore.GetAsync(cancellationToken);
        if (new DriveInfo(paths.RecordsDirectory).AvailableFreeSpace - MAX_CHUNK_BYTES <= StorageService.MediaThreshold(settings))
        {
            throw new InfrastructureException("转码上传所需存储空间不足。", 507);
        }
        var gate = Gate(jobId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, "transcoding-" + jobId, cancellationToken);
            await leases.RequireAsync(workerId, jobId, attempt, leaseToken, cancellationToken);
            var path = await UploadPathAsync(job, fileName, length, cancellationToken);
            await using var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                65536, FileOptions.Asynchronous);
            if (offset < 0 || offset != output.Length || offset > length)
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
                    if (written > MAX_CHUNK_BYTES || offset + written > length)
                    {
                        throw new InfrastructureException("上传块超过允许长度。", 413);
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await leases.RequireAsync(workerId, jobId, attempt, leaseToken, cancellationToken);
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

    /// <summary>校验所有字节、媒体参数和当前源版本，再发布不可变成品。</summary>
    public async Task CompleteAsync(string workerId, string jobId, int attempt, string leaseToken,
        TranscodeOutputManifest manifest, CancellationToken cancellationToken)
    {
        if (manifest.Files is null || manifest.Files.Count is < 2 or > 100000)
        {
            throw new InfrastructureException("转码完成清单无效。");
        }
        var gate = Gate(jobId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, "transcoding-" + jobId, cancellationToken);
            if (await ResumeCompletedAsync(workerId, jobId, attempt, leaseToken, cancellationToken))
            {
                return;
            }
            var job = await leases.RequireAsync(workerId, jobId, attempt, leaseToken, cancellationToken);
            await RequireManifestAsync(Staging(job), WorkerOutputManifestFingerprint.Create(manifest), cancellationToken);
            var asset = await assets.RequireActiveAsync(job.AssetId, job.SourceVersion, cancellationToken);
            var relative = Path.Combine(".derived", asset.Id, asset.SourceVersion, job.RecipeHash, job.Id + "-" + attempt.ToString(CultureInfo.InvariantCulture));
            var destination = MediaPaths.Resolve(paths.RecordsDirectory, relative);
            var directory = Directory.Exists(destination) ? destination : Staging(job);
            var verified = new List<RenditionArtifact>();
            foreach (var artifact in manifest.Files)
            {
                ValidateUpload(artifact.Name, artifact.Length, job.Profile);
                if (artifact.Sha256 is not { Length: 64 } || !artifact.Sha256.All(char.IsAsciiHexDigit))
                {
                    throw new InfrastructureException("转码文件校验值无效。");
                }
                var file = new FileInfo(Path.Combine(directory, artifact.Name));
                if (!file.Exists || file.Length != artifact.Length)
                {
                    throw new InfrastructureException("转码产物尚未完整上传。", 409);
                }
                await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                if (!string.Equals(hash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InfrastructureException("转码文件内容校验失败。", 422);
                }
                verified.Add(new() { Name = artifact.Name, Length = artifact.Length, Sha256 = hash, WriteTicks = file.LastWriteTimeUtc.Ticks });
            }
            await TranscodeArtifactPolicy.ValidatePlaylistAsync(directory, manifest.Files, cancellationToken, job.Profile);
            await context.Collection<TranscodeJob>("transcodeJobs").UpdateOneAsync(
                TranscodeLeaseService.Fence(workerId, jobId, attempt, leaseToken),
                Builders<TranscodeJob>.Update.Set(x => x.State, "Publishing").Set(x => x.Stage, "Validating"), cancellationToken: cancellationToken);
            var source = asset.MediaInfo ?? await inspector.InspectAsync(assets.SourcePath(asset), cancellationToken);
            if (asset.MediaInfo is null)
            {
                await context.Collection<MediaAsset>("mediaAssets").UpdateOneAsync(x => x.Id == asset.Id && x.SourceVersion == asset.SourceVersion,
                    Builders<MediaAsset>.Update.Set(x => x.MediaInfo, source), cancellationToken: cancellationToken);
            }
            var media = await inspector.InspectAsync(Path.Combine(directory, "index.m3u8"), cancellationToken);
            await TranscodeArtifactPolicy.ValidatePlaylistAsync(directory, manifest.Files, cancellationToken, job.Profile, media.FrameRate);
            ValidateMedia(job.Profile, source, media);
            var rendition = new PlaybackRendition
            {
                Id = job.Id + "-" + attempt.ToString(CultureInfo.InvariantCulture),
                AssetId = asset.Id, SourceVersion = asset.SourceVersion,
                ProfileId = job.Profile.Id, ProfileName = job.ProfileName, RecipeHash = job.RecipeHash,
                RelativeDirectory = relative, JobId = job.Id, Attempt = attempt,
                DurationMs = media.DurationMs, SourceStartMs = source.SourceStartMs,
                MediaEndTimestampMs = media.RawVideoEndMs,
                PlaybackTimestampOffsetMs = source.SourceStartMs - media.RawVideoStartMs!.Value
                    - Math.Max(0, media.FirstVideoSampleDurationMs!.Value - 1000 / Math.Min(source.FrameRate, job.Profile.MaxFrameRate ?? source.FrameRate)),
                Width = media.Width, Height = media.Height, FrameRate = media.FrameRate,
                Bytes = verified.Sum(x => x.Length), Artifacts = verified
            };
            await JsonFiles.WriteAsync(Path.Combine(directory, "completed.json"), rendition, cancellationToken);
            await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
            job = await leases.RequireAsync(workerId, jobId, attempt, leaseToken, cancellationToken);
            if (job.PublicationDirectory is { } previous && previous != relative)
            {
                await RemoveUnpublishedDirectoryAsync(previous, cancellationToken);
            }
            var publication = await context.Collection<TranscodeJob>("transcodeJobs").UpdateOneAsync(
                TranscodeLeaseService.Fence(workerId, jobId, attempt, leaseToken),
                Builders<TranscodeJob>.Update.Set(x => x.PublicationDirectory, relative), cancellationToken: cancellationToken);
            if (publication.MatchedCount == 0)
            {
                throw new InfrastructureException("转码发布租约已失效。", 409);
            }
            if (!Directory.Exists(destination))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.Move(directory, destination);
            }
            var jobUpdate = await context.Collection<TranscodeJob>("transcodeJobs").UpdateOneAsync(
                TranscodeLeaseService.Fence(workerId, jobId, attempt, leaseToken),
                Builders<TranscodeJob>.Update.Set(x => x.State, "Completed").Set(x => x.Stage, "Completed")
                    .Set(x => x.Progress, 100).Set(x => x.FinishedAtUtc, DateTime.UtcNow)
                    .Set(x => x.LeaseUntilUtc, null).Set(x => x.Error, null), cancellationToken: cancellationToken);
            if (jobUpdate.ModifiedCount == 0)
            {
                throw new InfrastructureException("转码发布租约已失效。", 409);
            }
            await new TranscodeRenditionStore(context).PublishAsync(rendition, cancellationToken);
            await MarkPublishedAsync(job.Id, attempt, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>提交退队、跳过或失败状态，同时释放执行租约。</summary>
    public async Task FinishAsync(string workerId, string jobId, int attempt, string leaseToken,
        WorkerJobResult result, CancellationToken cancellationToken)
    {
        if (result.State is not ("Failed" or "Skipped" or "Queued"))
        {
            throw new InfrastructureException("转码任务终态无效。");
        }
        var gate = Gate(jobId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, "transcoding-" + jobId, cancellationToken);
            await leases.RequireAsync(workerId, jobId, attempt, leaseToken, cancellationToken);
            var update = result.State == "Queued" ? TranscodeJobScheduler.Reset()
                : Builders<TranscodeJob>.Update.Set(x => x.State, result.State).Set(x => x.Stage, result.State)
                    .Set(x => x.LeaseTokenHash, null).Set(x => x.LeaseUntilUtc, null).Set(x => x.FinishedAtUtc, DateTime.UtcNow);
            var stages = WorkerStageSnapshotPolicy.Normalize("Transcoding", result.Stages);
            if (stages.Count > 0)
            {
                update = update.Set(x => x.Stages, stages);
            }
            await context.Collection<TranscodeJob>("transcodeJobs").UpdateOneAsync(
                TranscodeLeaseService.Fence(workerId, jobId, attempt, leaseToken),
                update.Set(x => x.Error, Limit(result.Error, 2048)).Set(x => x.ErrorCode, Limit(result.ErrorCode, 100)),
                cancellationToken: cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> ResumeCompletedAsync(string workerId, string jobId, int attempt, string leaseToken,
        CancellationToken cancellationToken)
    {
        var hash = TranscodeWorkerService.SecretHash(leaseToken);
        var job = await context.Collection<TranscodeJob>("transcodeJobs").Find(x => x.Id == jobId && x.Attempt == attempt
            && x.WorkerId == workerId && x.State == "Completed" && !x.CancelRequested && x.LeaseTokenHash == hash)
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            return false;
        }
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        await assets.RequireActiveAsync(job.AssetId, job.SourceVersion, cancellationToken);
        var relative = Path.Combine(".derived", job.AssetId, job.SourceVersion, job.RecipeHash,
            job.Id + "-" + attempt.ToString(CultureInfo.InvariantCulture));
        var directory = MediaPaths.Resolve(paths.RecordsDirectory, relative);
        await using var stream = File.OpenRead(Path.Combine(directory, "completed.json"));
        var rendition = await JsonSerializer.DeserializeAsync<PlaybackRendition>(stream, JsonFiles.Options, cancellationToken);
        if (rendition is null || rendition.JobId != job.Id || rendition.Attempt != job.Attempt
            || rendition.Id != job.Id + "-" + attempt.ToString(CultureInfo.InvariantCulture) || rendition.RelativeDirectory != relative
            || rendition.AssetId != job.AssetId || rendition.SourceVersion != job.SourceVersion || rendition.RecipeHash != job.RecipeHash)
        {
            throw new InfrastructureException("转码完成清单无法恢复。", 409);
        }
        foreach (var artifact in rendition.Artifacts)
        {
            TranscodeArtifactPolicy.ValidateName(artifact.Name);
            var file = new FileInfo(Path.Combine(directory, artifact.Name));
            if (!file.Exists || file.Length != artifact.Length || file.LastWriteTimeUtc.Ticks != artifact.WriteTicks)
            {
                throw new InfrastructureException("转码成品已发生变化。", 410);
            }
        }
        var current = await context.Collection<TranscodeJob>("transcodeJobs").Find(x => x.Id == job.Id && x.Attempt == attempt
            && x.State == "Completed" && !x.CancelRequested).AnyAsync(cancellationToken);
        if (!current)
        {
            throw new InfrastructureException("转码任务状态已发生变化。", 409);
        }
        await new TranscodeRenditionStore(context).PublishAsync(rendition, cancellationToken);
        await MarkPublishedAsync(job.Id, attempt, cancellationToken);
        return true;
    }

    private async Task MarkPublishedAsync(string jobId, int attempt, CancellationToken cancellationToken)
    {
        var jobs = context.Collection<TranscodeJob>("transcodeJobs");
        var job = await jobs.Find(value => value.Id == jobId && value.Attempt == attempt && value.State == "Completed").FirstAsync(cancellationToken);
        await jobs.UpdateOneAsync(value => value.Id == jobId && value.Attempt == attempt && value.State == "Completed",
            Builders<TranscodeJob>.Update.Set(value => value.PublicationReady, true).Set(value => value.PublicationDirectory, null)
                .Set(value => value.Stages, WorkerStageSnapshotPolicy.Complete("Transcoding", job.Stages)), cancellationToken: cancellationToken);
    }

    internal static async Task RequireManifestAsync(string directory, string fingerprint, CancellationToken cancellationToken)
    {
        var marker = Path.Combine(directory, "upload-session.json");
        if (File.Exists(marker) && await File.ReadAllTextAsync(marker, cancellationToken) != fingerprint)
        {
            throw new InfrastructureException("完成清单与当前上传会话不符。", 409);
        }
    }

    private async Task RemoveUnpublishedDirectoryAsync(string relative, CancellationToken cancellationToken)
    {
        if (await context.Collection<PlaybackRendition>("playbackRenditions").Find(x => x.RelativeDirectory == relative).AnyAsync(cancellationToken))
        {
            return;
        }
        var directory = MediaPaths.Resolve(paths.RecordsDirectory, relative);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
        var id = Path.GetFileName(directory);
        await context.Collection<PlaybackRenditionFile>("renditionFiles").DeleteManyAsync(x => x.RenditionId == id, cancellationToken);
    }

    private async Task<string> UploadPathAsync(TranscodeJob job, string fileName, long length, CancellationToken cancellationToken)
    {
        var directory = Staging(job);
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, fileName + ".length");
        if (File.Exists(marker))
        {
            if (await File.ReadAllTextAsync(marker, cancellationToken) != length.ToString(CultureInfo.InvariantCulture))
            {
                throw new InfrastructureException("当前上传文件的总长度已发生变化。", 409);
            }
        }
        else
        {
            await PrivateFiles.WriteAllTextAsync(marker, length.ToString(CultureInfo.InvariantCulture), cancellationToken);
        }
        return Path.Combine(directory, fileName);
    }

    private string Staging(TranscodeJob job) => MediaPaths.Resolve(paths.RecordsDirectory,
        Path.Combine(".work", "transcoding", job.Id, job.Attempt.ToString(CultureInfo.InvariantCulture)));

    private SemaphoreSlim Gate(string jobId) => WorkerTransferLocks.Gate("Transcoding", jobId);

    private static void ValidateUpload(string fileName, long length, TranscodeProfile profile)
    {
        TranscodeArtifactPolicy.ValidateName(fileName);
        if (fileName != "index.m3u8" && profile.SingleFile != (fileName == "media.mp4"))
        {
            throw new InfrastructureException("转码文件与当前任务的存储模式不符。");
        }
        if (length <= 0 || fileName == "init.mp4" && length > 1024L * 1024 * 1024
            || fileName == "index.m3u8" && length > 16 * 1024 * 1024)
        {
            throw new InfrastructureException("转码文件长度无效。");
        }
    }

    private static void ValidateMedia(TranscodeProfile profile, TranscodeMediaInfo source, TranscodeMediaInfo media)
    {
        if (!double.IsFinite(source.SourceStartMs) || media.RawVideoStartMs is not { } rawStart || !double.IsFinite(rawStart)
            || media.FirstVideoSampleDurationMs is not { } firstDuration || !double.IsFinite(firstDuration) || firstDuration <= 0
            || media.RawVideoEndMs is not { } rawEnd || !double.IsFinite(rawEnd) || rawEnd <= rawStart)
        {
            throw new InfrastructureException("转码成品缺少可验证的首帧时间映射。", 422);
        }
        if (!double.IsFinite(source.DurationMs) || source.DurationMs <= 0 || !double.IsFinite(media.DurationMs) || media.DurationMs <= 0
            || source.Width <= 0 || source.Height <= 0 || media.Width <= 0 || media.Height <= 0
            || !double.IsFinite(source.FrameRate) || source.FrameRate <= 0 || !double.IsFinite(media.FrameRate) || media.FrameRate <= 0)
        {
            throw new InfrastructureException("转码媒体探测未得到有效的时长、尺寸或帧率。", 422);
        }
        if (Math.Min(source.Width, source.Height) < profile.Height)
        {
            throw new InfrastructureException("目标清晰度高于源媒体，此档位应跳过。", 422);
        }
        if (Math.Abs(Math.Min(media.Width, media.Height) - profile.Height) > 2
            || media.Width > source.Width || media.Height > source.Height
            || Math.Abs(media.Width / (double)media.Height - source.Width / (double)source.Height) > 0.02)
        {
            throw new InfrastructureException("转码成品尺寸或宽高比与源媒体、目标档位不符。", 422);
        }
        if (Math.Abs(source.DurationMs - media.DurationMs) > Math.Max(500, 2000 / source.FrameRate))
        {
            throw new InfrastructureException("转码成品时长与源媒体不符。", 422);
        }
        if (media.FrameRate > Math.Min(source.FrameRate, profile.MaxFrameRate ?? source.FrameRate) + 0.05)
        {
            throw new InfrastructureException("转码成品帧率超过源媒体或目标档位上限。", 422);
        }
        if (media.VideoCodec is not ("h264" or "avc") || media.IsHdr
            || source.HasAudio != media.HasAudio || media.HasAudio && media.AudioCodec != "aac")
        {
            throw new InfrastructureException("转码成品必须保留音轨并使用 H.264 SDR / AAC。", 422);
        }
        var videoLimit = TranscodeBitratePolicy.GetTargetBitrate(source.VideoBitrate, profile.VideoBitrateKbps);
        if (!TranscodeBitratePolicy.IsWithinLimit(media.VideoBitrate, videoLimit))
        {
            throw new InfrastructureException(FormattableString.Invariant($"转码成品视频平均码率超过源媒体或目标档位上限及 VBR 容差。实测 {media.VideoBitrate} bps，源媒体 {source.VideoBitrate} bps，档位上限 {profile.VideoBitrateKbps * 1000L} bps，允许上限 {TranscodeBitratePolicy.GetMaximumBitrate(videoLimit):0.##} bps。"), 422);
        }
        var audioLimit = TranscodeBitratePolicy.GetTargetBitrate(source.AudioBitrate, profile.AudioBitrateKbps);
        if (media.HasAudio && !TranscodeBitratePolicy.IsWithinLimit(media.AudioBitrate, audioLimit))
        {
            throw new InfrastructureException(FormattableString.Invariant($"转码成品音频平均码率超过源媒体或目标档位上限及 VBR 容差。实测 {media.AudioBitrate} bps，源媒体 {source.AudioBitrate} bps，档位上限 {profile.AudioBitrateKbps * 1000L} bps，允许上限 {TranscodeBitratePolicy.GetMaximumBitrate(audioLimit):0.##} bps。"), 422);
        }
    }

    private static string? Limit(string? value, int length) => value?.Length > length ? value[..length] : value;

    /// <summary>停止传输服务后释放同步资源。</summary>
    public void Dispose()
    {
    }
}
