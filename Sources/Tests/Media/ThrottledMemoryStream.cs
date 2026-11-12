namespace Yohuke.LiveRecorder.Media.Tests;

internal sealed class ThrottledMemoryStream(byte[] data) : MemoryStream(data, writable: false)
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(500, cancellationToken);
        return await base.ReadAsync(buffer[..Math.Min(4096, buffer.Length)], cancellationToken);
    }
}
