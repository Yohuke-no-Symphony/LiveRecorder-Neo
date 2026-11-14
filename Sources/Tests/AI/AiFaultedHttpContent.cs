using System.Net;

namespace Yohuke.LiveRecorder.AI.Tests;

internal sealed class AiFaultedHttpContent(Exception failure) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.FromException(failure);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
