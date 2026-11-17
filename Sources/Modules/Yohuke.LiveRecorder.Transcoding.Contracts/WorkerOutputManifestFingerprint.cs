using System.Security.Cryptography;
using System.Text.Json;
using Yohuke.LiveRecorder.Clipping.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>以规范化内容清单区分可续传的同一成品和需要重置的重建产物。</summary>
public static class WorkerOutputManifestFingerprint
{
    /// <summary>计算与文件枚举顺序及校验值大小写无关的转码产物指纹。</summary>
    public static string Create(TranscodeOutputManifest manifest) => Hash(manifest.Files
        .OrderBy(file => file.Name, StringComparer.Ordinal)
        .Select(file => new { file.Name, file.Length, Sha256 = file.Sha256.ToLowerInvariant() }).ToArray());

    /// <summary>计算包含精确时间范围的切片产物指纹。</summary>
    public static string Create(ClipOutputManifest manifest) => Hash(new
    {
        manifest.Bytes, Sha256 = manifest.Sha256.ToLowerInvariant(), manifest.ActualStartMs, manifest.ActualEndMs
    });

    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(value, WorkerProtocol.JsonOptions)));
}
