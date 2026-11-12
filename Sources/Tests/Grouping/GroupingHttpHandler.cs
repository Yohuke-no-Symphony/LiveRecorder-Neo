using System.Net;

namespace Yohuke.LiveRecorder.Grouping.Tests;

internal sealed class GroupingHttpHandler(byte[] data) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
    }
}
