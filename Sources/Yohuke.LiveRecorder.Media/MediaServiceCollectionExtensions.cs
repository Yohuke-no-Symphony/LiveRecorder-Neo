using Microsoft.Extensions.DependencyInjection;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media;

/// <summary>Registers recording and media processing without coupling them to HTTP endpoints.</summary>
public static class MediaServiceCollectionExtensions
{
    /// <summary>Registers the shared process limits, recorder, processor, and playback window builder.</summary>
    public static IServiceCollection AddLiveRecorderMedia(this IServiceCollection services, Action<MediaToolOptions>? configure = null)
    {
        services.AddOptions<MediaToolOptions>()
            .Validate(options => options.ProbeConcurrency > 0
                && options.StreamConnectTimeout > TimeSpan.Zero
                && options.StreamReadTimeout > TimeSpan.Zero
                && options.PlaybackWindowDuration > TimeSpan.Zero
                && options.HlsSegmentDuration > TimeSpan.Zero, "Media deadlines, probe concurrency, and playback durations must be positive.")
            .ValidateOnStart();
        if (configure is not null)
        {
            services.Configure(configure);
        }
        services.AddSingleton<MediaProcessRunner>();
        services.AddSingleton<IMediaProcessor, FfmpegMediaProcessor>();
        services.AddSingleton<IPlaybackWindowBuilder, IndexedPlaybackWindowBuilder>();
        services.AddHttpClient<FlvMediaRecorder>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddTransient<IMediaRecorder>(provider => provider.GetRequiredService<FlvMediaRecorder>());
        return services;
    }
}
