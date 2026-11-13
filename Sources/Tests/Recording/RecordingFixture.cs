using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Storage;
using Yohuke.LiveRecorder.Media;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Tests;

internal sealed class RecordingFixture : IAsyncDisposable
{
    private HttpClient client = null!;
    private RecordingSupervisor? supervisor;
    private MediaJobWorker? jobWorker;
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "live-recorder-recording-tests", Guid.NewGuid().ToString("N"));
    internal AppPaths Paths { get; private set; } = null!;
    internal string StoragePath => Paths.RecordsDirectory;
    internal string DatabaseName { get; } = "recording_tests_" + Guid.NewGuid().ToString("N");
    internal string ConnectionString { get; } = Environment.GetEnvironmentVariable("LIVE_RECORDER_TEST_MONGO")!;
    internal ControlledBilibiliService Bilibili { get; } = new();
    internal IStatusPublisher Publisher { get; } = new NullRecordingPublisher();
    internal BootstrapStore Bootstrap { get; private set; } = null!;
    internal MongoContext Context { get; private set; } = null!;
    internal ISettingsStore Settings { get; private set; } = null!;
    internal FlvMediaRecorder Recorder { get; private set; } = null!;
    internal MediaProcessRunner Processes { get; private set; } = null!;
    internal FfmpegMediaProcessor Processor { get; private set; } = null!;
    internal IndexedPlaybackWindowBuilder Playback { get; private set; } = null!;
    internal LibraryService Library { get; private set; } = null!;
    internal SessionGroupingService Grouping { get; private set; } = null!;
    internal MediaJobService Jobs { get; private set; } = null!;
    internal long SampleBytes { get; private set; }

    internal static async Task<RecordingFixture> CreateAsync(double durationSeconds = 3, string size = "160x90", int fps = 10)
    {
        var fixture = new RecordingFixture();
        try
        {
            fixture.Paths = new(Path.Combine(fixture.DirectoryPath, "data"), Path.Combine(fixture.DirectoryPath, "records"));
            fixture.Paths.EnsureDirectories();
            Directory.CreateDirectory(fixture.StoragePath);
            var startup = new StartupConfigurationStore(Path.Combine(fixture.DirectoryPath, "startup.json"));
            await startup.SaveAsync(fixture.Paths.DataDirectory, fixture.Paths.RecordsDirectory);
            fixture.Bootstrap = new(fixture.Paths, new DataProtectionSecretProtector(new EphemeralDataProtectionProvider()));
            await fixture.Bootstrap.SaveAsync(new() { Installed = true, DatabaseName = fixture.DatabaseName, MongoConnectionString = fixture.ConnectionString });
            fixture.Context = new(fixture.Bootstrap);
            fixture.Settings = new SettingsStore(startup, fixture.Bootstrap, fixture.Context, fixture.Paths);
            await fixture.Settings.SaveAsync(new ServerSettings { MinimumFreeBytes = 0, SegmentBytes = 0, MonitorIntervalSeconds = 1, SessionGraceMinutes = 10, PlaybackConcurrency = 4, MediaConcurrency = 1 });
            var data = await fixture.CreateSampleAsync(durationSeconds, size, fps);
            fixture.SampleBytes = data.Length;
            var chunks = ParseChunks(data);
            fixture.client = new(new ControlledStreamHandler(data, chunks, fixture.Bilibili)) { Timeout = Timeout.InfiniteTimeSpan };
            var options = Options.Create(new MediaToolOptions { HlsSegmentDuration = TimeSpan.FromSeconds(1), StreamReadTimeout = TimeSpan.FromSeconds(10) });
            fixture.Recorder = new(fixture.client, options, fixture.Settings);
            fixture.Processes = new(options, fixture.Settings);
            fixture.Processor = new(fixture.Processes);
            fixture.Playback = new(fixture.Processes, options);
            fixture.Library = new(fixture.Context, TimeProvider.System);
            fixture.Grouping = new(fixture.Context, fixture.Library, fixture.Settings, fixture.Publisher, fixture.Paths);
            fixture.Jobs = new(fixture.Context, fixture.Library, fixture.Publisher);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    internal async Task<(Streamer Target, ControlledRoom Room)> AddRoomAsync(long id)
    {
        var room = new ControlledRoom(id);
        Bilibili.Rooms[id] = room;
        var target = new Streamer { RoomId = id, Uid = id + 100000, Name = $"测试主播 {id}", DirectoryName = $"测试主播_{id}", Enabled = true };
        await Context.Collection<Streamer>("streamers").InsertOneAsync(target);
        return (target, room);
    }

    internal async Task StartSupervisorAsync()
    {
        Assert.Null(supervisor);
        supervisor = new(Context, Bootstrap, Settings, Bilibili, Recorder, Publisher, NullLogger<RecordingSupervisor>.Instance, Grouping, Paths);
        await supervisor.StartAsync(CancellationToken.None);
    }

    internal async Task StopSupervisorAsync()
    {
        if (supervisor is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await supervisor.StopAsync(timeout.Token);
            supervisor.Dispose();
            supervisor = null;
        }
    }

    internal async Task StartJobWorkerAsync()
    {
        jobWorker = new(Context, Bootstrap, Settings, Processor, Publisher, new StorageService(Context, Settings, Paths), NullLogger<MediaJobWorker>.Instance, Paths);
        await jobWorker.StartAsync(CancellationToken.None);
    }

    internal Task<List<BroadcastSession>> SessionsAsync() => Context.Collection<BroadcastSession>("sessions").Find(FilterDefinition<BroadcastSession>.Empty).ToListAsync();
    internal Task<List<RecordingSegment>> SegmentsAsync() => Context.Collection<RecordingSegment>("segments").Find(FilterDefinition<RecordingSegment>.Empty).ToListAsync();

    internal static async Task WaitUntilAsync(Func<Task<bool>> predicate, string description, int timeoutSeconds = 15)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            if (await predicate())
            {
                return;
            }
            await Task.Delay(100);
        }
        Assert.Fail("Timed out: " + description);
    }

    internal async Task AssertCommittedFilesAsync(bool requireClosed)
    {
        var sessions = (await SessionsAsync()).ToDictionary(x => x.Id);
        foreach (var segment in await SegmentsAsync())
        {
            if (requireClosed)
            {
                Assert.True(segment.IsClosed);
            }
            var directory = MediaPaths.Resolve(StoragePath, sessions[segment.SessionId].DirectoryPath);
            var path = string.IsNullOrEmpty(segment.RootRelativePath) ? MediaPaths.Resolve(directory, segment.RelativePath) : MediaPaths.Resolve(StoragePath, segment.RootRelativePath);
            Assert.True(File.Exists(path));
            Assert.InRange(segment.CommittedBytes, 13, new FileInfo(path).Length);
            AssertTagBoundary(path, segment.CommittedBytes);
            if (requireClosed)
            {
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        }
    }

    private async Task<byte[]> CreateSampleAsync(double durationSeconds, string size, int fps)
    {
        var path = Path.Combine(DirectoryPath, "source.flv");
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"testsrc=size={size}:rate={fps}", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", durationSeconds.ToString(CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "ultrafast", "-threads", "1", "-g", fps.ToString(CultureInfo.InvariantCulture), "-keyint_min", fps.ToString(CultureInfo.InvariantCulture), "-sc_threshold", "0", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "64k", "-y", path })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        Assert.True(process.ExitCode == 0, await error);
        return await File.ReadAllBytesAsync(path);
    }

    private static IReadOnlyList<FlvReadChunk> ParseChunks(byte[] data)
    {
        Assert.Equal("FLV", System.Text.Encoding.ASCII.GetString(data, 0, 3));
        var chunks = new List<FlvReadChunk> { new(0, 13, 0) };
        var offset = 13;
        var latest = 0L;
        while (offset < data.Length)
        {
            var size = (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
            var timestamp = (long)((uint)data[offset + 4] << 16 | (uint)data[offset + 5] << 8 | data[offset + 6] | (uint)data[offset + 7] << 24);
            latest = Math.Max(latest, timestamp);
            chunks.Add(new(offset, size + 15, latest));
            offset += size + 15;
        }
        Assert.Equal(data.Length, offset);
        return chunks;
    }

    private static void AssertTagBoundary(string path, long committed)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[13];
        stream.ReadExactly(header);
        Assert.Equal("FLV", System.Text.Encoding.ASCII.GetString(header, 0, 3));
        var tagHeader = new byte[11];
        var previousSize = new byte[4];
        while (stream.Position < committed)
        {
            stream.ReadExactly(tagHeader);
            var size = (tagHeader[1] << 16) | (tagHeader[2] << 8) | tagHeader[3];
            Assert.True(stream.Position + size + 4 <= committed);
            stream.Position += size;
            stream.ReadExactly(previousSize);
            Assert.Equal((uint)size + 11, BinaryPrimitives.ReadUInt32BigEndian(previousSize));
        }
        Assert.Equal(committed, stream.Position);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopSupervisorAsync();
            if (jobWorker is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await jobWorker.StopAsync(timeout.Token);
                jobWorker.Dispose();
            }
        }
        finally
        {
            client?.Dispose();
            Processes?.Dispose();
            if (DatabaseName.StartsWith("recording_tests_", StringComparison.Ordinal) && DatabaseName.Length == 48)
            {
                await new MongoClient(ConnectionString).DropDatabaseAsync(DatabaseName);
            }
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, true);
            }
        }
    }
}
