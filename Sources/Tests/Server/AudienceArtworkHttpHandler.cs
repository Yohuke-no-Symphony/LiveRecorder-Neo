using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Yohuke.LiveRecorder.Tests;

internal sealed class AudienceArtworkHttpHandler : HttpMessageHandler, IHttpClientFactory
{
    internal ConcurrentQueue<(Uri Source, string? Referer, string UserAgent)> Requests { get; } = new();

    /// <inheritdoc />
    public HttpClient CreateClient(string name) => new(this, false);

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue((request.RequestUri!, request.Headers.Referrer?.AbsoluteUri, request.Headers.UserAgent.ToString()));
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(request.RequestUri!.AbsolutePath));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
