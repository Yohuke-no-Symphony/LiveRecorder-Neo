using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Tests;

public sealed class MediaPathsTests
{
    [Theory]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("直播：今天/开心?", "直播：今天_开心_")]
    [InlineData("...", "未命名")]
    public void NamesArePortable(string input, string expected)
    {
        Assert.Equal(expected, MediaPaths.SafeName(input));
    }

    [Fact]
    public void ParentTraversalIsRejected()
    {
        Assert.Throws<InfrastructureException>(() => MediaPaths.Resolve(Path.GetTempPath(), "../outside.flv"));
    }

    [Fact]
    public void LongUnicodeTitlesLeaveRoomForTimestampAndStableId()
    {
        var title = MediaPaths.SafeName(new string('录', 200));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(title) <= 120);
        Assert.DoesNotContain('\uFFFD', title);
    }
}
