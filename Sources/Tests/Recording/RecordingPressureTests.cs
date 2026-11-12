using System.Diagnostics;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Xunit;
using Xunit.Abstractions;

namespace Yohuke.LiveRecorder.Recording.Tests;

/// <summary>真实持续采集与派生媒体任务并行的显式压力回归。</summary>
public sealed class RecordingPressureTests(ITestOutputHelper output)
{
    [RecordingStressFact]
    public async Task ThirtyTwoContinuousRecordingsStayLiveWithFourPlaybackWindowsAndOneClip()
    {
        await using var fixture = await RecordingFixture.CreateAsync(85, "320x180", 20);
        for (var index = 0; index < 32; index++)
        {
            await fixture.AddRoomAsync(1000 + index);
        }
        await fixture.StartSupervisorAsync();
        await RecordingFixture.WaitUntilAsync(async () => fixture.Bilibili.Rooms.Values.All(x => x.ActiveStreams == 1) && (await fixture.SegmentsAsync()).Count(x => x.DurationMs >= 3000 && x.CommittedBytes > 13) == 32, "all 32 real FLV streams have committed three seconds", 25);
        var sessions = await fixture.SessionsAsync();
        Assert.Equal(32, sessions.Count);
        using var stopPlaybacks = new CancellationTokenSource();
        var playbackCount = 0;
        async Task PlaybackLoopAsync(BroadcastSession session, int index)
        {
            var attempt = 0;
            while (!stopPlaybacks.IsCancellationRequested)
            {
                var segment = await fixture.Context.Collection<RecordingSegment>("segments").Find(x => x.SessionId == session.Id).SortByDescending(x => x.DurationMs).FirstAsync(stopPlaybacks.Token);
                var directory = MediaPaths.Resolve(fixture.StoragePath, session.DirectoryPath);
                var target = Path.Combine(fixture.DirectoryPath, "playback", index.ToString(), (++attempt).ToString());
                var result = await fixture.Playback.BuildAsync(new PlaybackWindowRequest
                {
                    SourcePath = MediaPaths.Resolve(directory, segment.RelativePath),
                    IndexPath = MediaPaths.Resolve(directory, segment.IndexRelativePath),
                    CommittedBytes = segment.CommittedBytes,
                    IsClosed = segment.IsClosed,
                    SourceStartMs = Math.Max(0, segment.DurationMs - 4000),
                    DurationMs = 2000,
                    OutputDirectory = target
                }, stopPlaybacks.Token);
                Assert.True(File.Exists(result.PlaylistPath));
                Assert.True(result.DurationMs > 0);
                Interlocked.Increment(ref playbackCount);
                if (attempt > 1)
                {
                    Directory.Delete(Path.Combine(fixture.DirectoryPath, "playback", index.ToString(), (attempt - 1).ToString()), true);
                }
                await Task.Delay(1000, stopPlaybacks.Token);
            }
        }
        var clip = await fixture.Jobs.CreateAsync(new CreateMediaJobRequest { SessionId = sessions[0].Id, Kind = "Clip", Mode = "Exact", Title = "压力测试切片", StartMs = 500, EndMs = 2500 }, "pressure-viewer", false);
        var playbacks = sessions.Take(4).Select((session, index) => PlaybackLoopAsync(session, index)).ToArray();
        var samples = new List<(double Seconds, long Managed, long Working)>();
        var previous = fixture.Bilibili.Rooms.ToDictionary(x => x.Key, x => x.Value.ReadBytes);
        var clock = Stopwatch.StartNew();
        try
        {
            await fixture.StartJobWorkerAsync();
            while (clock.Elapsed < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                foreach (var (id, room) in fixture.Bilibili.Rooms)
                {
                    Assert.Equal(1, room.ActiveStreams);
                    Assert.Equal(1, room.Connections);
                    Assert.True(room.ReadBytes > previous[id], $"Room {id} stopped ingesting while derived media tasks ran.");
                    previous[id] = room.ReadBytes;
                }
                var segments = await fixture.SegmentsAsync();
                Assert.Equal(32, segments.Count);
                Assert.All(segments, segment => Assert.True(segment.CommittedBytes > 13));
                using var process = Process.GetCurrentProcess();
                var managed = GC.GetTotalMemory(true);
                process.Refresh();
                samples.Add((clock.Elapsed.TotalSeconds, managed, process.WorkingSet64));
                output.WriteLine($"t={clock.Elapsed.TotalSeconds:F1}s streams=32 sourceBytes={previous.Values.Sum()} managedMiB={managed / 1048576d:F1} workingMiB={process.WorkingSet64 / 1048576d:F1} playbackWindows={Volatile.Read(ref playbackCount)}");
                foreach (var playback in playbacks)
                {
                    Assert.False(playback.IsFaulted, playback.Exception?.ToString());
                }
            }
        }
        finally
        {
            await stopPlaybacks.CancelAsync();
            foreach (var playback in playbacks)
            {
                try
                {
                    await playback;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        await RecordingFixture.WaitUntilAsync(async () => (await fixture.Context.Collection<MediaJob>("jobs").Find(x => x.Id == clip.Id).SingleAsync()).State == "Completed", "real background clip completes", 10);
        var published = await fixture.Context.Collection<Clip>("clips").Find(x => x.JobId == clip.Id).SingleAsync();
        var probe = await fixture.Processor.ProbeAsync(MediaPaths.Resolve(fixture.StoragePath, published.RelativePath));
        Assert.InRange(probe.DurationMs, 1500, 3000);
        Assert.True(Volatile.Read(ref playbackCount) >= 16);
        var warmed = samples.Where(x => x.Seconds >= 10).ToList();
        Assert.True(warmed[^1].Managed - warmed[0].Managed < 128L * 1024 * 1024, "Retained managed memory exceeded the 128 MiB growth budget after warmup.");
        Assert.True(warmed[^1].Working - warmed[0].Working < 256L * 1024 * 1024, "Working set exceeded the 256 MiB growth budget after warmup.");
        await fixture.StopSupervisorAsync();
        Assert.All(fixture.Bilibili.Rooms.Values, room => { Assert.Equal(0, room.ActiveStreams); Assert.Equal(0, room.ActiveSubscriptions); });
        await fixture.AssertCommittedFilesAsync(true);
        output.WriteLine($"PASS: elapsed={clock.Elapsed.TotalSeconds:F1}s rooms=32 sessions={(await fixture.SessionsAsync()).Count} playbackWindows={playbackCount} clipBytes={published.Bytes} sampleBytes={fixture.SampleBytes} managedGrowthMiB={(warmed[^1].Managed - warmed[0].Managed) / 1048576d:F1} workingGrowthMiB={(warmed[^1].Working - warmed[0].Working) / 1048576d:F1}");
    }
}
