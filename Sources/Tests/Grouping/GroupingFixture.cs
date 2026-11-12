using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Media;

namespace Yohuke.LiveRecorder.Grouping.Tests;

internal sealed class GroupingFixture : IAsyncDisposable
{
    internal static DateTime Origin { get; } = new(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "live-recorder-grouping-tests", Guid.NewGuid().ToString("N"));
    internal string DatabaseName { get; } = "grouping_tests_" + Guid.NewGuid().ToString("N");
    internal MongoContext Mongo { get; }
    internal GroupingSettingsStore Settings { get; }
    internal LibraryService Library { get; }
    internal GroupingStatusPublisher Publisher { get; } = new();
    internal MediaProcessRunner Processes { get; }
    internal FfmpegMediaProcessor Processor { get; }
    private BootstrapStore Bootstrap { get; }
    private string Connection { get; } = Environment.GetEnvironmentVariable("LIVE_RECORDER_TEST_MONGO") ?? "mongodb://127.0.0.1:34784";

    private GroupingFixture()
    {
        Directory.CreateDirectory(Root);
        Bootstrap = new(new AppPaths(Path.Combine(Root, ".config")), new GroupingSecretProtector());
        Mongo = new(Bootstrap);
        Settings = new(Root);
        Library = new(Mongo, TimeProvider.System);
        Processes = new(Options.Create(new MediaToolOptions()), Settings);
        Processor = new(Processes);
    }

    internal static async Task<GroupingFixture> CreateAsync()
    {
        var fixture = new GroupingFixture();
        await fixture.Bootstrap.SaveAsync(new()
        {
            Installed = true, MongoConnectionString = fixture.Connection, DatabaseName = fixture.DatabaseName, StoragePath = fixture.Root
        });
        await fixture.Mongo.Database.RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1));
        await fixture.Mongo.Collection<Streamer>("streamers").InsertOneAsync(new Streamer { Id = "streamer", Uid = 42, RoomId = 42, Name = "主播", DirectoryName = "主播_42" });
        return fixture;
    }

    internal SessionGroupingService Service() => new(Mongo, new LibraryService(Mongo, TimeProvider.System), Settings, Publisher);

    internal async Task<SessionManifest> SeedAsync(double startMs = 0, IReadOnlyList<(double Offset, double Duration)>? spans = null, string streamerId = "streamer")
    {
        spans ??= [(0, 1000), (1500, 500)];
        var session = new BroadcastSession
        {
            StreamerId = streamerId, RoomId = 42, Title = "测试场次", State = "Completed", StartedAtUtc = Origin.AddMilliseconds(startMs),
            EndedAtUtc = Origin.AddMilliseconds(startMs + spans.Max(span => span.Offset + span.Duration))
        };
        session.DirectoryPath = Path.Combine("主播_42", session.Id);
        var manifest = new SessionManifest { Session = session };
        for (var index = 0; index < spans.Count; index++)
        {
            var relative = Path.Combine("source", "录像_" + index + ".flv");
            var path = Path.Combine(Root, session.DirectoryPath, relative);
            var indexRelative = Path.ChangeExtension(relative, ".index.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "canonical-source-" + session.Id + "-" + index);
            await File.WriteAllTextAsync(Path.Combine(Root, session.DirectoryPath, indexRelative), "{\"kind\":\"Keyframe\",\"offset\":13}\n");
            manifest.Segments.Add(new()
            {
                SessionId = session.Id, RelativePath = relative, IndexRelativePath = indexRelative, IsClosed = true,
                StartOffsetMs = spans[index].Offset, DurationMs = spans[index].Duration, CommittedBytes = new FileInfo(path).Length
            });
        }
        await Mongo.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        await Mongo.Collection<RecordingSegment>("segments").InsertManyAsync(manifest.Segments);
        await JsonFiles.WriteAsync(Path.Combine(Root, session.DirectoryPath, "场次.json"), manifest);
        return manifest;
    }

    internal async Task WriteEventsAsync(BroadcastSession session, params double[] offsets)
    {
        await using (var writer = new SessionEventWriter(Path.Combine(Root, session.DirectoryPath, "弹幕")))
        {
            for (var index = 0; index < offsets.Length; index++)
            {
                await writer.WriteAsync(new()
                {
                    EventId = session.Id + "_event_" + index, OffsetMs = offsets[index], ReceivedAtUtc = session.StartedAtUtc.AddMilliseconds(offsets[index]),
                    Kind = index % 2 == 0 ? "Danmu" : "SC", Text = "event-" + index
                });
            }
        }
        session.HasDanmu = true;
        await Mongo.Collection<BroadcastSession>("sessions").UpdateOneAsync(item => item.Id == session.Id, Builders<BroadcastSession>.Update.Set(item => item.HasDanmu, true));
    }

    internal async Task<SessionManifest> SeedNativeAsync()
    {
        var session = new BroadcastSession
        {
            StreamerId = "streamer", RoomId = 42, Title = "native", State = "Completed", StartedAtUtc = Origin
        };
        session.DirectoryPath = Path.Combine("主播_42", session.Id);
        var directory = Path.Combine(Root, session.DirectoryPath);
        Directory.CreateDirectory(directory);
        var sample = Path.Combine(Root, "fixture.flv");
        var info = new ProcessStartInfo(Settings.Value.FfmpegPath) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=10", "-f", "lavfi", "-i", "sine=sample_rate=48000",
            "-t", "2", "-c:v", "libx264", "-preset", "ultrafast", "-g", "10", "-pix_fmt", "yuv420p", "-c:a", "aac", "-y", sample
        })
        {
            info.ArgumentList.Add(argument);
        }
        using (var process = Process.Start(info)!)
        {
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(error);
            }
        }
        using var client = new HttpClient(new GroupingHttpHandler(await File.ReadAllBytesAsync(sample)));
        var recorder = new FlvMediaRecorder(client, Options.Create(new MediaToolOptions()), Settings);
        var updates = new List<MediaRecordingUpdate>();
        await recorder.RecordAsync(new()
        {
            SessionId = session.Id, StreamUrl = "https://example.invalid/live.flv", OutputDirectory = directory, Title = session.Title,
            StartedAtUtc = Origin, SessionStartedAtUtc = Origin, MaxSegmentBytes = 1
        }, new GroupingProgress<MediaRecordingUpdate>(updates.Add), CancellationToken.None);
        var manifest = new SessionManifest { Session = session };
        manifest.Segments = updates.Where(update => update.Kind == "Closed").Select(update => new RecordingSegment
        {
            Id = update.SegmentId, SessionId = session.Id, RelativePath = update.RelativePath, IndexRelativePath = update.IndexRelativePath,
            StartOffsetMs = update.StartOffsetMs, DurationMs = update.DurationMs, CommittedBytes = update.CommittedBytes, IsClosed = true, Codec = update.Codec
        }).ToList();
        session.EndedAtUtc = Origin.AddMilliseconds(manifest.Segments.Max(segment => segment.StartOffsetMs + segment.DurationMs));
        await Mongo.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        await Mongo.Collection<RecordingSegment>("segments").InsertManyAsync(manifest.Segments);
        await JsonFiles.WriteAsync(Path.Combine(directory, "场次.json"), manifest);
        return manifest;
    }

    internal Dictionary<string, string> ArchiveHashes()
    {
        return Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".flv", StringComparison.Ordinal) || path.EndsWith(".jsonl", StringComparison.Ordinal))
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);
    }

    internal static SessionManifest Project(SessionManifest source, bool right, double boundary)
    {
        var target = new BroadcastSession
        {
            StreamerId = source.Session.StreamerId, RoomId = source.Session.RoomId, Title = right ? "right" : "left",
            StartedAtUtc = right ? source.Session.StartedAtUtc.AddMilliseconds(boundary) : source.Session.StartedAtUtc,
            EndedAtUtc = right ? source.Session.EndedAtUtc : source.Session.StartedAtUtc.AddMilliseconds(boundary),
            GroupedSessionIds = [source.Session.Id], ProjectionReady = false, State = "Completed"
        };
        target.DirectoryPath = Path.Combine("主播_42", target.Id);
        return new()
        {
            Session = target,
            Segments = source.Segments.Where(segment => right ? segment.StartOffsetMs >= boundary : segment.StartOffsetMs < boundary).Select(segment => new RecordingSegment
            {
                Id = target.Id + "_" + segment.Id, SessionId = target.Id, IsClosed = true,
                RelativePath = segment.RelativePath, IndexRelativePath = segment.IndexRelativePath,
                RootRelativePath = Path.Combine(source.Session.DirectoryPath, segment.RelativePath),
                RootIndexRelativePath = Path.Combine(source.Session.DirectoryPath, segment.IndexRelativePath),
                StartOffsetMs = segment.StartOffsetMs - (right ? boundary : 0), DurationMs = segment.DurationMs, CommittedBytes = segment.CommittedBytes
            }).ToList()
        };
    }

    public async ValueTask DisposeAsync()
    {
        Processes.Dispose();
        try
        {
            await new MongoClient(Connection).DropDatabaseAsync(DatabaseName);
        }
        finally
        {
            Directory.Delete(Root, true);
        }
    }
}
