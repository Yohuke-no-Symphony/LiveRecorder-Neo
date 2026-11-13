using System.Security.Cryptography;
using System.Text;
using Yohuke.DanmuCore.Apis;
using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>在鉴权媒体端点之后缓存 B站公开头像和封面。</summary>
public sealed class ArtworkCache(IHttpClientFactory clients, IAppPaths paths)
{
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 16).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>取得允许来源的图片，拒绝非图像与过大响应。</summary>
    public async Task<string?> GetAsync(string sourceUrl, CancellationToken cancellationToken = default)
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
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl))).ToLowerInvariant();
        var root = Path.Combine(paths.CacheDirectory, "artwork");
        var path = Path.Combine(root, key + ".image");
        var gate = gates[(int)(Convert.ToUInt32(key[..8], 16) % gates.Length)];
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path) && File.Exists(path + ".type"))
            {
                return path;
            }
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
            Directory.CreateDirectory(root);
            var temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, output.ToArray(), cancellationToken);
            await File.WriteAllTextAsync(path + ".type.tmp", type, cancellationToken);
            File.Move(path + ".type.tmp", path + ".type", true);
            File.Move(temporary, path, true);
            return path;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            gate.Release();
        }
    }
}
