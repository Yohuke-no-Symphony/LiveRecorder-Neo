using System.Collections.Concurrent;
using System.Net;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class RecordingLiveTestHttpHandler(byte[] bytes, TimeSpan readDelay) : HttpMessageHandler
{
    private readonly ConcurrentBag<RecordingLiveTestStream> streams = [];
    internal long ReadBytes => streams.Sum(stream => stream.ReadBytes);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stream = new RecordingLiveTestStream(bytes, readDelay);
        streams.Add(stream);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
    }
}
