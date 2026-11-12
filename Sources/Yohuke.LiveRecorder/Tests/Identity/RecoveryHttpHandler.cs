using System.Net;
using System.Text;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class RecoveryHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    internal static HttpResponseMessage Json(string data) => new(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "application/json") };
    internal static Task<HttpResponseMessage> EmptyAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.Contains("gethistory", StringComparison.Ordinal) ? "{\"code\":0,\"data\":{\"room\":[]}}" : "{\"code\":0,\"data\":{\"list\":[]}}"));
}
