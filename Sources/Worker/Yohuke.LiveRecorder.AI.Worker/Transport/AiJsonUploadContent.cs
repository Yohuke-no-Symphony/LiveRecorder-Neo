using System.Net;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.AI.Worker.Transport;

internal sealed class AiJsonUploadContent : HttpContent
{
    private readonly byte[] bytes;
    private readonly TransferThrottle throttle;
    private readonly long bytesPerSecond;

    internal AiJsonUploadContent(byte[] bytes, TransferThrottle throttle, long bytesPerSecond)
    {
        this.bytes = bytes;
        this.throttle = throttle;
        this.bytesPerSecond = bytesPerSecond;
        Headers.ContentType = new("application/json");
    }

    protected override bool TryComputeLength(out long length)
    {
        length = bytes.Length;
        return true;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, CancellationToken.None);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => WriteAsync(stream, cancellationToken);

    private async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < bytes.Length;)
        {
            var count = Math.Min(65536, bytes.Length - offset);
            await throttle.WaitAsync(count, bytesPerSecond, cancellationToken);
            await stream.WriteAsync(bytes.AsMemory(offset, count), cancellationToken);
            offset += count;
        }
    }
}
