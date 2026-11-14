using System.Net;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class PlaybackRecordingSource(byte[] bytes) : HttpMessageHandler, IProgress<MediaRecordingUpdate>
{
    internal List<MediaRecordingUpdate> Updates { get; } = [];

    public void Report(MediaRecordingUpdate value)
    {
        Updates.Add(value);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        });
    }
}
