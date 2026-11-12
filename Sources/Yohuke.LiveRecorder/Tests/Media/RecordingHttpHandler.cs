using System.Net;

namespace Yohuke.LiveRecorder.Media.Tests;

internal sealed class RecordingHttpHandler(byte[] data) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data)
        });
    }
}
