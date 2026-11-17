using System.Buffers.Binary;
using System.Text.Json;

namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>长度前缀的 JSON 控制协议；媒体内容通过 HTTPS 独立传输。</summary>
public static class WorkerProtocol
{
    public const int MAX_MESSAGE_BYTES = 1024 * 1024;
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>读取完整控制帧，拒绝无界数据和不兼容版本。</summary>
    public static async Task<WorkerControlMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken);
        if (first == 0)
        {
            return null;
        }
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MAX_MESSAGE_BYTES)
        {
            throw new InvalidDataException("Invalid worker message length.");
        }
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        var message = JsonSerializer.Deserialize<WorkerControlMessage>(body, JsonOptions)
            ?? throw new InvalidDataException("Missing worker message.");
        if (message.Version != 1)
        {
            throw new InvalidDataException("Unsupported worker protocol version.");
        }
        return message;
    }

    /// <summary>向独占写入的连接发送有界控制帧。</summary>
    public static async Task WriteAsync<T>(Stream stream, string type, string requestId, T payload, CancellationToken cancellationToken)
    {
        var message = new WorkerControlMessage
        {
            Type = type,
            RequestId = requestId,
            Payload = JsonSerializer.SerializeToElement(payload, JsonOptions)
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (body.Length > MAX_MESSAGE_BYTES)
        {
            throw new InvalidDataException("Worker message exceeds the protocol limit.");
        }
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
