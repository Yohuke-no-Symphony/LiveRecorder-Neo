namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class RoomSearchHttpHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return handle(request, cancellationToken);
    }
}
