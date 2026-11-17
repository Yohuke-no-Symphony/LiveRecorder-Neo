using System.Text.Json;
using Xunit;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media.Tests;

/// <summary>验证分数毫秒场次起点在多次自动封口后仍保留完全相同的相邻边界。</summary>
public sealed class FlvArchiveTargetProviderTests
{
    /// <summary>新段起点复用前段终点，精确切分边界不会被估算尾部覆盖或制造微小空档。</summary>
    [Theory]
    [InlineData(1L)]
    [InlineData(1234567L)]
    public void ConsecutiveSegmentsShareExactlyTheSameFractionalBoundary(long initialOffsetTicks)
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-target-timing-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var sessionStarted = DateTime.UnixEpoch.AddTicks(123456789);
            var updates = new List<MediaRecordingUpdate>();
            var target = new FlvArchiveTargetProvider(new()
            {
                OutputDirectory = directory, StartedAtUtc = sessionStarted.AddTicks(initialOffsetTicks),
                SessionStartedAtUtc = sessionStarted, FileTimeZoneId = "UTC"
            }, new InlineProgress<MediaRecordingUpdate>(updates.Add));
            var expectedStart = TimeSpan.FromTicks(initialOffsetTicks).TotalMilliseconds;
            double[] durations = [14386, 2000, 4057, 999, 2000];
            foreach (var duration in durations)
            {
                var output = target.CreateOutputStream();
                using var media = output.stream;
                var current = Assert.IsType<FlvOutputState>(output.state);
                using var index = current.IndexWriter;
                Assert.Equal(expectedStart, current.StartOffsetMs);
                if (target.Outputs.Count > 0)
                {
                    var previous = target.Outputs[^1];
                    Assert.Equal(previous.StartOffsetMs + previous.DurationMs, current.StartOffsetMs);
                }
                current.DurationMs = duration - 1;
                current.EstimatedEndMs = duration + 33.333333;

                target.CloseAt(duration, 0);
                target.PrepareClose();
                target.Complete();

                var archived = target.Outputs[^1];
                var closed = updates[^1];
                expectedStart = archived.StartOffsetMs + duration;
                Assert.Equal(duration, archived.DurationMs);
                Assert.Equal("Closed", closed.Kind);
                Assert.Equal(duration, closed.DurationMs);
                using var commit = JsonDocument.Parse(File.ReadLines(current.IndexPath).Last());
                Assert.Equal(duration, commit.RootElement.GetProperty("timestampMs").GetDouble());
                Assert.Equal(expectedStart, commit.RootElement.GetProperty("sessionOffsetMs").GetDouble());
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
