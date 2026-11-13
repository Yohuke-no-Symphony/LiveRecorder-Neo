namespace Yohuke.LiveRecorder.Media;

internal sealed class MediaProcessInputStream : Stream
{
    private readonly Stream inner;

    internal MediaProcessInputStream(Stream inner)
    {
        this.inner = inner;
    }

    internal Exception? WriteFailure { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Flush()
    {
        try
        {
            inner.Flush();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            WriteFailure = exception;
            throw;
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await inner.FlushAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            WriteFailure = exception;
            throw;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        try
        {
            inner.Write(buffer, offset, count);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            WriteFailure = exception;
            throw;
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.WriteAsync(buffer, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            WriteFailure = exception;
            throw;
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
