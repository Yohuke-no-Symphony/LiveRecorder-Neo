using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Tests;

public sealed class StoragePolicyTests
{
    [Fact]
    public void DerivedMediaStopsBeforeRecordingWatermark()
    {
        var settings = new ServerSettings { MinimumFreeBytes = 1000 };
        Assert.Equal(1250, StorageService.MediaThreshold(settings));
    }

    [Fact]
    public void LargeWatermarkCannotOverflowAndDisableProtection()
    {
        Assert.Equal(long.MaxValue, StorageService.MediaThreshold(new ServerSettings { MinimumFreeBytes = long.MaxValue }));
    }
}
