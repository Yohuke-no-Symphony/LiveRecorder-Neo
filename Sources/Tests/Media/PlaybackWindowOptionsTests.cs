using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media.Tests;

/// <summary>Checks the shared window duration configuration at the dependency injection boundary.</summary>
public sealed class PlaybackWindowOptionsTests
{
    /// <summary>Invalid window durations fail configuration validation before requests are handled.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(121)]
    public void RejectsUnsupportedWindowDuration(double seconds)
    {
        var services = new ServiceCollection().AddLiveRecorderMedia();
        services.Configure<PlaybackWindowOptions>(options => options.Duration = TimeSpan.FromSeconds(seconds));
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<PlaybackWindowOptions>>().Value);
    }

    /// <summary>The complete supported range is available through the shared application options.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(120)]
    public void AcceptsSupportedWindowDuration(double seconds)
    {
        var services = new ServiceCollection().AddLiveRecorderMedia();
        services.Configure<PlaybackWindowOptions>(options => options.Duration = TimeSpan.FromSeconds(seconds));
        using var provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromSeconds(seconds), provider.GetRequiredService<IOptions<PlaybackWindowOptions>>().Value.Duration);
    }
}
