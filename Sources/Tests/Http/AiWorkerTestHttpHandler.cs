namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class AiWorkerTestHttpHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond = null,
    Func<HttpRequestMessage, HttpResponseMessage, CancellationToken, Task>? observe = null)
    : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
{
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = respond is null
            ? await base.SendAsync(request, cancellationToken)
            : await respond(request, cancellationToken);
        try
        {
            if (observe is not null)
            {
                await observe(request, response, cancellationToken);
            }
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
