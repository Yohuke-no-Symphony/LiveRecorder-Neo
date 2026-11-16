namespace Yohuke.LiveRecorder.Clipping.Worker.Tests;

internal sealed class ClipTestHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        handle(request, cancellationToken);
}
