using System.Net;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Transport;

internal sealed class UploadChunkContent(Stream source, long length, TransferThrottle throttle, long bytesPerSecond,
    TransferProgressTimeout timeout) : HttpContent
{
    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    /// <inheritdoc />
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var remaining = length;
        var buffer = new byte[16384];
        while (remaining > 0)
        {
            var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException("The output artifact changed during upload.");
            }
            using (timeout.Pause())
            {
                await throttle.WaitAsync(count, bytesPerSecond, cancellationToken);
            }
            await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            timeout.Progress();
            remaining -= count;
        }
    }

    /// <inheritdoc />
    protected override bool TryComputeLength(out long contentLength)
    {
        contentLength = length;
        return true;
    }
}
