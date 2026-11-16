namespace Yohuke.LiveRecorder.Recording.Worker.Capture;

internal sealed class RecordingHttpClientFactory : IHttpClientFactory
{
    /// <inheritdoc />
    public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(20) };
}
