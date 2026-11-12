using System.Net;

namespace Yohuke.LiveRecorder.Recording.Tests;

internal sealed class ControlledStreamHandler(byte[] data, IReadOnlyList<FlvReadChunk> chunks, ControlledBilibiliService bilibili) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var id = long.Parse(Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath), System.Globalization.CultureInfo.InvariantCulture);
        var room = bilibili.Rooms[id];
        var selected = room.DisconnectAfterMs is { } cutoff ? chunks.Where(x => x.TimestampMs <= cutoff).ToArray() : chunks;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PacedFlvStream(data, selected, room)) });
    }
}
