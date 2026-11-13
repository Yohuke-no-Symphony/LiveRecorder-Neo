using System.Net;

namespace Yohuke.LiveRecorder.Recording.Tests;

internal sealed class ControlledStreamHandler(byte[] data, IReadOnlyList<FlvReadChunk> chunks, ControlledBilibiliService bilibili) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var id = long.Parse(Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath), System.Globalization.CultureInfo.InvariantCulture);
        var room = bilibili.Rooms[id];
        room.RequestStream();
        if (room.StreamFailure is { } failure)
        {
            throw failure;
        }
        var selected = room.DisconnectAfterMs is { } cutoff ? chunks.Where(x => x.TimestampMs <= cutoff).ToArray() : chunks;
        if (room.HeaderOnly)
        {
            selected = chunks.Where(IsHeaderChunk).ToArray();
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PacedFlvStream(data, selected, room)) });
    }

    private bool IsHeaderChunk(FlvReadChunk chunk)
    {
        if (chunk.Offset == 0 || data[chunk.Offset] == 18)
        {
            return true;
        }
        return data[chunk.Offset] is 8 or 9 && chunk.Length > 16 && data[chunk.Offset + 12] == 0;
    }
}
