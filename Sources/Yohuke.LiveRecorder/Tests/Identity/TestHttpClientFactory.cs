namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class TestHttpClientFactory : IHttpClientFactory
{
    internal Func<HttpRequestMessage, Task<HttpResponseMessage>> Handle { get; set; } = _ => throw new InvalidOperationException("Unexpected HTTP request.");
    public HttpClient CreateClient(string name) => new(new StubHttpHandler(request => Handle(request)));
}
