using System.Collections.Concurrent;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Tests;

internal sealed class SessionCoverTestFrameExtractor : IVideoFrameExtractor
{
    internal ConcurrentQueue<VideoFrameRequest> Requests { get; } = new();
    internal Func<VideoFrameRequest, CancellationToken, Task<bool>> Handle { get; set; } = SaveAsync;

    /// <inheritdoc />
    public Task<bool> TryExtractAsync(VideoFrameRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Enqueue(request);
        return Handle(request, cancellationToken);
    }

    internal static async Task<bool> SaveAsync(VideoFrameRequest request, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
        await File.WriteAllBytesAsync(request.OutputPath, [0xff, 0xd8, 0xff, 0xd9], cancellationToken);
        return true;
    }
}
