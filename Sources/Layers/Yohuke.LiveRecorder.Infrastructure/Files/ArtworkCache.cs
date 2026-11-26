using System.Security.Cryptography;
using System.Text;
using Yohuke.DanmuCore.Apis;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>在鉴权媒体端点之后缓存头像、封面及本地生成的图片。</summary>
public sealed class ArtworkCache(IHttpClientFactory clients, IAppPaths paths) : IDisposable
{
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 16).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly Lock sync = new();
    private readonly Dictionary<string, int> readers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取得允许来源的图片与读取租约，拒绝非图像与过大响应。</summary>
    public async Task<ArtworkCacheLease?> GetAsync(string sourceUrl, CancellationToken cancellationToken = default)
    {
        if (sourceUrl.StartsWith("//", StringComparison.Ordinal))
        {
            sourceUrl = "https:" + sourceUrl;
        }
        if (sourceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            sourceUrl = "https://" + sourceUrl[7..];
        }
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !(uri.Host.EndsWith(".hdslb.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        try
        {
            return await GetOrCreateAsync(sourceUrl, (destination, token) => DownloadAsync(uri, destination, token), cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    internal Task<ArtworkCacheLease?> GetGeneratedJpegAsync(string identity, Func<string, CancellationToken, Task<bool>> generate,
        CancellationToken cancellationToken) => GetOrCreateAsync(identity, async (destination, token) =>
            await generate(destination, token) ? "image/jpeg" : null, cancellationToken);

    private async Task<ArtworkCacheLease?> GetOrCreateAsync(string identity, Func<string, CancellationToken, Task<string?>> create,
        CancellationToken cancellationToken)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var root = Path.Combine(paths.CacheDirectory, "artwork");
        var path = Path.Combine(root, key + ".image");
        var gate = Gate(key);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path) && File.Exists(path + ".type"))
            {
                var cachedType = await File.ReadAllTextAsync(path + ".type", cancellationToken);
                return CreateLease(path, cachedType, key);
            }
            Directory.CreateDirectory(root);
            var temporary = path + ".tmp";
            var temporaryType = path + ".type.tmp";
            try
            {
                var type = await create(temporary, cancellationToken);
                if (type is null)
                {
                    return null;
                }
                await File.WriteAllTextAsync(temporaryType, type, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryType, path + ".type", true);
                File.Move(temporary, path, true);
                return CreateLease(path, type, key);
            }
            finally
            {
                File.Delete(temporary);
                File.Delete(temporaryType);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string?> DownloadAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        using var client = clients.CreateClient("artwork");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", BaseApi.DEFAULT_USER_AGENT);
        request.Headers.Referrer = new("https://live.bilibili.com/");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 8 * 1024 * 1024)
        {
            return null;
        }
        var type = response.Content.Headers.ContentType?.MediaType;
        if (type is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif"))
        {
            return null;
        }
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > 8 * 1024 * 1024)
            {
                return null;
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await File.WriteAllBytesAsync(destination, output.ToArray(), cancellationToken);
        return type;
    }

    /// <summary>清理满足未使用时间要求且没有下载或读取租约的已知图片缓存。</summary>
    public async Task<CacheCleanupCategoryResult> CleanupAsync(int minimumUnusedMinutes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumUnusedMinutes);
        var cutoff = CacheCleanupFileSystem.Cutoff(minimumUnusedMinutes);
        var result = new CacheCleanupCategoryResult { Category = CacheCleanupCategories.ARTWORK };
        var root = Path.Combine(paths.CacheDirectory, "artwork");
        var fileSystem = new CacheCleanupFileSystem(paths.CacheDirectory);
        var files = fileSystem.ReadFiles(root, result, cancellationToken);
        var known = new List<FileInfo>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsKnownFile(file.Name))
            {
                known.Add(file);
            }
            else
            {
                result.SkippedEntries++;
            }
        }
        foreach (var group in known.GroupBy(file => file.Name[..64], StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = group.ToArray();
            var gate = Gate(group.Key);
            if (!await gate.WaitAsync(0, cancellationToken))
            {
                result.SkippedEntries += entries.Length;
                continue;
            }
            try
            {
                lock (sync)
                {
                    if (readers.ContainsKey(group.Key))
                    {
                        result.SkippedEntries += entries.Length;
                        continue;
                    }
                }
                if (!fileSystem.IsSafeDirectory(root, result))
                {
                    continue;
                }
                try
                {
                    foreach (var file in entries)
                    {
                        file.Refresh();
                    }
                    var marker = entries.FirstOrDefault(file => file.Name.EndsWith(".image.type", StringComparison.Ordinal));
                    var lastUsed = marker is { Exists: true }
                        ? marker.LastWriteTimeUtc
                        : entries.Where(file => file.Exists).Select(file => file.LastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
                    if (minimumUnusedMinutes > 0 && lastUsed >= cutoff)
                    {
                        result.SkippedEntries += entries.Length;
                        continue;
                    }
                    foreach (var file in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        fileSystem.DeleteFile(file, result);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    result.FailedEntries++;
                }
            }
            finally
            {
                gate.Release();
            }
        }
        return result;
    }

    private SemaphoreSlim Gate(string key) => gates[(int)(Convert.ToUInt32(key[..8], 16) % gates.Length)];

    private ArtworkCacheLease CreateLease(string path, string contentType, string key)
    {
        File.SetLastWriteTimeUtc(path + ".type", DateTime.UtcNow);
        lock (sync)
        {
            readers.TryGetValue(key, out var count);
            readers[key] = count + 1;
        }
        return new(path, contentType, () => ReleaseReader(key));
    }

    private void ReleaseReader(string key)
    {
        lock (sync)
        {
            if (readers[key] == 1)
            {
                readers.Remove(key);
            }
            else
            {
                readers[key]--;
            }
        }
    }

    private static bool IsKnownFile(string name)
    {
        return name.Length > 64 && name.AsSpan(0, 64).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0
            && name[64..] is ".image" or ".image.type" or ".image.tmp" or ".image.type.tmp";
    }

    /// <summary>在宿主停止图片请求和清理后释放分片同步资源。</summary>
    public void Dispose()
    {
        foreach (var gate in gates)
        {
            gate.Dispose();
        }
    }
}
