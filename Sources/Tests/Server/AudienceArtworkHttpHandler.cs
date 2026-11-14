using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Yohuke.LiveRecorder.Tests;

internal sealed class AudienceArtworkHttpHandler : HttpMessageHandler, IHttpClientFactory
{
    internal ConcurrentQueue<(Uri Source, string? Referer, string UserAgent)> Requests { get; } = new();
    internal HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
    internal Func<Stream>? ImageStreamFactory { get; set; }

    /// <inheritdoc />
    public HttpClient CreateClient(string name) => new(this, false);

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue((request.RequestUri!, request.Headers.Referrer?.AbsoluteUri, request.Headers.UserAgent.ToString()));
        HttpContent content = ImageStreamFactory is null
            ? new ByteArrayContent(Encoding.UTF8.GetBytes(request.RequestUri!.AbsolutePath))
            : new StreamContent(ImageStreamFactory());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return Task.FromResult(new HttpResponseMessage(StatusCode) { Content = content });
    }
}
