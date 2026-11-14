using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.AI;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.AI.Tests;

internal sealed class AiMongoFixture : IAsyncDisposable
{
    internal AiTestWorkspace Workspace { get; } = new();
    internal DataProtectionSecretProtector Protector { get; } = new(new EphemeralDataProtectionProvider());
    internal BootstrapStore Bootstrap { get; }
    internal MongoContext Mongo { get; }
    internal AiTestSettingsStore Settings { get; }
    internal AiTestStatusPublisher Publisher { get; } = new();
    internal LibraryService Library { get; }
    internal LiveMarkerFileStore MarkerFiles { get; }
    internal AiLiveMarkerPublisher MarkerPublisher { get; }
    internal IMongoCollection<AiAnalysisJob> Jobs => Mongo.Collection<AiAnalysisJob>("ai_analysis_jobs");
    private string DatabaseName { get; } = "ai_tests_" + Guid.NewGuid().ToString("N");

    private AiMongoFixture()
    {
        Bootstrap = new(Workspace.Paths, Protector);
        Mongo = new(Bootstrap);
        Settings = new(Workspace.Settings);
        Library = new(Mongo, TimeProvider.System);
        MarkerFiles = new(Workspace.Paths);
        MarkerPublisher = new(Library, Workspace.Paths, MarkerFiles, new());
    }

    internal static async Task<AiMongoFixture> CreateAsync()
    {
        var fixture = new AiMongoFixture();
        await fixture.Bootstrap.SaveAsync(new()
        {
            Installed = true,
            MongoConnectionString = Environment.GetEnvironmentVariable("LIVE_RECORDER_TEST_MONGO") ?? "mongodb://127.0.0.1:34784",
            DatabaseName = fixture.DatabaseName
        });
        await fixture.Mongo.Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        return fixture;
    }

    internal AiAnalysisService Service(IMongoContext? context = null) => new(context ?? Mongo, Library, Settings, Workspace.Data, Publisher, MarkerPublisher);

    internal AiAnalysisWorker Worker(IAiAnalysisRunner runner) => new(Mongo, Bootstrap, Settings, Protector, runner, Publisher, NullLogger<AiAnalysisWorker>.Instance, Workspace.Paths, MarkerPublisher);

    internal async Task<BroadcastSession> SeedAsync(bool ended = true, double duration = 120000, List<EventArchiveSource>? sources = null)
    {
        var detail = AiTestWorkspace.Session(duration);
        detail.Session.DirectoryPath = detail.Session.Id;
        detail.Session.EventSources = sources ?? [];
        if (!ended)
        {
            detail.Session.EndedAtUtc = null;
            detail.Session.State = "Recording";
        }
        await Workspace.WriteAsync(detail.Session.DirectoryPath, new RecordedEventEnvelope { Kind = "Danmu", OffsetMs = 1000, Text = "T:歌曲介绍" });
        await Mongo.Collection<BroadcastSession>("sessions").InsertOneAsync(detail.Session);
        await Mongo.Collection<RecordingSegment>("segments").InsertOneAsync(new()
        {
            SessionId = detail.Session.Id, DurationMs = duration, IsClosed = ended, RelativePath = "video.flv"
        });
        return detail.Session;
    }

    internal async Task<AiAnalysisJob> WaitForTerminalAsync(string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (true)
        {
            var job = await Jobs.Find(item => item.Id == id).SingleAsync(timeout.Token);
            if (job.State is not ("Running" or "Queued"))
            {
                return job;
            }
            await Task.Delay(30, timeout.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Mongo.Database.Client.DropDatabaseAsync(DatabaseName);
        Workspace.Dispose();
    }
}
