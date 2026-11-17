using System.Security.Cryptography;
using System.Text.Json;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library.LiveMarkers;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain.LiveMarkers;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;

/// <summary>在场次目录内保存完整附件；所有变更由调用方持有媒体库锁。</summary>
public sealed class LiveMarkerFileStore(IAppPaths paths)
{
    public const string DIRECTORY_NAME = "直播记录";
    public const string ORIGINAL_FILE_NAME = "原始.yaml";
    public const string METADATA_FILE_NAME = "元数据.json";
    public const string DOCUMENT_FILE_NAME = "条目.json";

    /// <summary>读取一个场次的稳定文件清单。</summary>
    public async Task<IReadOnlyList<LiveMarkerFileMetadata>> ListAsync(string sessionDirectory,
        CancellationToken cancellationToken = default)
    {
        var directory = DirectoryPath(sessionDirectory);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var files = new List<LiveMarkerFileMetadata>();
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var id = Path.GetFileName(child);
            if (Guid.TryParseExact(id, "N", out _))
            {
                files.Add(await ReadMetadataAsync(sessionDirectory, id, cancellationToken));
            }
        }

        return files.OrderByDescending(file => file.UploadedAtUtc).ThenBy(file => file.Id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>读取已发布附件的归属与校准。</summary>
    public async Task<LiveMarkerFileMetadata> ReadMetadataAsync(string sessionDirectory, string fileId,
        CancellationToken cancellationToken = default)
    {
        var directory = FileDirectory(sessionDirectory, fileId);
        if (!Directory.Exists(directory))
        {
            throw new InfrastructureException(MessageCode.LIVE_MARKER_NOT_FOUND, 404);
        }

        var file = await ReadJsonAsync<LiveMarkerFileMetadata>(Path.Combine(directory, METADATA_FILE_NAME),
            cancellationToken);
        if (file.Id != fileId || !Guid.TryParseExact(file.OriginId, "N", out _) ||
            file.EntryCount is < 0 or > LiveMarkerLimits.MAXIMUM_ENTRIES ||
            file.ContentHash.Length != 64 || string.IsNullOrEmpty(file.UploaderId))
        {
            throw CorruptFile();
        }

        return file;
    }

    /// <summary>读取上传时已固定的解析快照，不使用当前站点时区重新解释。</summary>
    public async Task<LiveMarkerDocument> ReadDocumentAsync(string sessionDirectory, LiveMarkerFileMetadata file,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(FileDirectory(sessionDirectory, file.Id), DOCUMENT_FILE_NAME);
        var document = await ReadJsonAsync<LiveMarkerDocument>(path, cancellationToken);
        if (document.Entries is null || document.Entries.Count != file.EntryCount ||
            document.Entries.Cast<LiveMarkerEntry?>().Any(entry => entry is null))
        {
            throw CorruptFile();
        }

        return document;
    }

    /// <summary>完整写入同卷暂存后一次性发布，原文件字节不变。</summary>
    public async Task PublishAsync(string sessionDirectory, ReadOnlyMemory<byte> original, LiveMarkerDocument document,
        LiveMarkerFileMetadata file, CancellationToken cancellationToken = default)
    {
        var destination = FileDirectory(sessionDirectory, file.Id);
        var staging = WorkDirectory("staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(staging, ORIGINAL_FILE_NAME), original.ToArray(),
                cancellationToken);
            await JsonFiles.WriteAsync(Path.Combine(staging, DOCUMENT_FILE_NAME), document, cancellationToken);
            await JsonFiles.WriteAsync(Path.Combine(staging, METADATA_FILE_NAME), file, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(staging, destination);
        }
        finally
        {
            TryCleanup(staging);
        }
    }

    /// <summary>原子替换文件级校准，保留原始 YAML 和条目。</summary>
    public Task WriteMetadataAsync(string sessionDirectory, LiveMarkerFileMetadata file,
        CancellationToken cancellationToken = default)
    {
        return JsonFiles.WriteAsync(Path.Combine(FileDirectory(sessionDirectory, file.Id), METADATA_FILE_NAME), file,
            cancellationToken);
    }

    /// <summary>以移出可见附件目录为删除提交点，清理失败交给维护恢复。</summary>
    public void Delete(string sessionDirectory, string fileId)
    {
        MoveToCleanup(FileDirectory(sessionDirectory, fileId));
    }

    /// <summary>场次彻底退役时移除其附件，即使源媒体目录仍被其它输出引用。</summary>
    public void DeleteAll(string sessionDirectory)
    {
        MoveToCleanup(DirectoryPath(sessionDirectory));
    }

    /// <summary>规划来源、原文和校准均相同的附件去重及独立目标身份。</summary>
    public async Task<IReadOnlyList<LiveMarkerProjectionCopy>> PlanCopiesAsync(string sourceDirectory,
        string targetSessionId,
        string targetDirectory, ISet<string> seen, CancellationToken cancellationToken = default)
    {
        var copies = new List<LiveMarkerProjectionCopy>();
        foreach (var file in await ListAsync(sourceDirectory, cancellationToken))
        {
            var identity = string.Join(':', file.OriginId, file.ContentHash,
                file.CalibrationMs.ToString(System.Globalization.CultureInfo.InvariantCulture), file.SourceTimeZoneId);
            if (!seen.Add(identity))
            {
                continue;
            }

            copies.Add(new()
            {
                SourceSessionDirectory = sourceDirectory,
                SourceFileId = file.Id,
                TargetSessionId = targetSessionId,
                TargetSessionDirectory = targetDirectory,
                File = file with { Id = Guid.NewGuid().ToString("N") }
            });
        }

        return copies;
    }

    /// <summary>恢复一个已记录固定目标身份的副本，不重写已发布副本。</summary>
    public async Task CompleteCopyAsync(LiveMarkerProjectionCopy copy, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(FileDirectory(copy.TargetSessionDirectory, copy.File.Id)))
        {
            var existing = await ReadMetadataAsync(copy.TargetSessionDirectory, copy.File.Id, cancellationToken);
            if (existing != copy.File)
            {
                throw CorruptFile();
            }

            await ReadDocumentAsync(copy.TargetSessionDirectory, existing, cancellationToken);
            await ReadOriginalAsync(copy.TargetSessionDirectory, existing, cancellationToken);
            return;
        }

        var source = await ReadMetadataAsync(copy.SourceSessionDirectory, copy.SourceFileId, cancellationToken);
        if (source with { Id = copy.File.Id } != copy.File)
        {
            throw CorruptFile();
        }

        var document = await ReadDocumentAsync(copy.SourceSessionDirectory, source, cancellationToken);
        var bytes = await ReadOriginalAsync(copy.SourceSessionDirectory, source, cancellationToken);
        await PublishAsync(copy.TargetSessionDirectory, bytes, document, copy.File, cancellationToken);
    }

    /// <summary>在媒体库锁下清理已提交删除和崩溃遗留的未发布暂存。</summary>
    public async Task MaintainAsync(CancellationToken cancellationToken = default)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        foreach (var kind in new[] { "staging", "deleting" })
        {
            var root = MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(".work", "live-markers", kind));
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var safe = WorkDirectory(kind, Path.GetFileName(directory));
                TryCleanup(safe);
            }
        }
    }

    /// <summary>计算不可变原文指纹，供副本去重及恢复校验使用。</summary>
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>读取并校验不可变原文，缺失或内容哈希不符时拒绝恢复。</summary>
    public async Task<byte[]> ReadOriginalAsync(string sessionDirectory, LiveMarkerFileMetadata file,
        CancellationToken cancellationToken = default)
    {
        var originalPath = Path.Combine(FileDirectory(sessionDirectory, file.Id), ORIGINAL_FILE_NAME);
        if (!File.Exists(originalPath) || new FileInfo(originalPath).Length > LiveMarkerLimits.MAXIMUM_BYTES)
        {
            throw CorruptFile();
        }

        var bytes = await File.ReadAllBytesAsync(originalPath, cancellationToken);
        if (Hash(bytes) != file.ContentHash)
        {
            throw CorruptFile();
        }

        return bytes;
    }

    private string DirectoryPath(string sessionDirectory)
    {
        var directory = MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(sessionDirectory, DIRECTORY_NAME));
        RejectDirectoryLink(directory);
        return directory;
    }

    private string FileDirectory(string sessionDirectory, string fileId)
    {
        if (!Guid.TryParseExact(fileId, "N", out _))
        {
            throw new InfrastructureException(MessageCode.LIVE_MARKER_NOT_FOUND, 404);
        }

        var directory =
            MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(sessionDirectory, DIRECTORY_NAME, fileId));
        RejectDirectoryLink(directory);
        return directory;
    }

    private string WorkDirectory(string kind, string id)
    {
        var directory = MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(".work", "live-markers", kind, id));
        RejectDirectoryLink(directory);
        return directory;
    }

    private void MoveToCleanup(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var destination = WorkDirectory("deleting", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(directory, destination);
        TryCleanup(destination);
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 65536, true);
            return await JsonSerializer.DeserializeAsync<T>(input, JsonFiles.Options, cancellationToken) ??
                   throw CorruptFile();
        }
        catch (Exception error) when (error is JsonException or FileNotFoundException or DirectoryNotFoundException)
        {
            throw CorruptFile();
        }
    }

    private static void RejectDirectoryLink(string directory)
    {
        if (Directory.Exists(directory) && new DirectoryInfo(directory).LinkTarget is not null)
        {
            throw new InfrastructureException(MessageCode.PATH_MEDIA_DIRECTORY_SYMLINK);
        }
    }

    private static void TryCleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static InfrastructureException CorruptFile() => new(MessageCode.LIVE_MARKER_FILE_CORRUPT, 409);
}
