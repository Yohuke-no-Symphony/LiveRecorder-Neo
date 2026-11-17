using System.Security.Cryptography;
using System.Text.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Worker.Common.Storage;

internal static class WorkerContentFingerprint
{
    internal static string Create<T>(T content) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content, WorkerProtocol.JsonOptions)));
}
