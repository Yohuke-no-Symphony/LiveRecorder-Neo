using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.AI;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;

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
    internal TranscodeWorkerService Workers { get; }
    internal AiJobExecutionService Execution { get; }
    internal AiJobScheduler Scheduler { get; }
    private AiInputManifestFactory Manifests { get; }
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
        Manifests = new(Workspace.Paths, Workspace.Data);
        Workers = new(Mongo, Workspace.Paths);
        var probes = new AiConnectionTestCoordinator();
        Execution = new(Mongo, Settings, Workspace.Paths, Workers, Library, Protector, Publisher, MarkerPublisher, Manifests, probes);
        Scheduler = new(Mongo, Settings, Workspace.Paths, Workers, Execution, probes);
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

    internal AiAnalysisService Service(IMongoContext? context = null) => new(context ?? Mongo, Library, Settings, Workspace.Data, Publisher, MarkerPublisher, Manifests);

    internal AiNodeTestHost Worker(IAiAnalysisRunner runner) => new(this, runner);

    internal async Task<TranscodeWorker> RegisterNodeAsync(CancellationToken cancellationToken = default)
    {
        var created = await Workers.CreateAsync("AI test node", "AI", cancellationToken);
        await Workers.ConnectAsync(new()
        {
            WorkerId = created.Worker.Id, Secret = created.Secret, Kind = "AI",
            ExpectedConfigurationRevision = created.Worker.ConfigurationRevision,
            InitialConfiguration = new()
            {
                TempDirectory = Workspace.Paths.DataDirectory, MinimumFreeBytes = 0, MaxConcurrentEncodes = 1
            },
            Capabilities = new() { AiExecutionVersion = 1 }
        }, cancellationToken);
        return await Workers.RequireAsync(created.Worker.Id, cancellationToken);
    }

    internal async Task PrepareSnapshotAsync(AiAnalysisJob job, IReadOnlyDictionary<string, string>? sourcePaths = null,
        CancellationToken cancellationToken = default)
    {
        var input = job.Input ?? Manifests.Legacy(job);
        sourcePaths ??= input.Sources.Select((source, index) => new
        {
            source.Id,
            Path = Path.Combine(input.PreparedSnapshot ? Workspace.Paths.DataDirectory : Workspace.Paths.RecordsDirectory,
                job.SourcePaths[index])
        }).ToDictionary(source => source.Id, source => source.Path);
        job.Snapshot = await Workspace.BuildSnapshotAsync(AiExecutionMapper.ToExecution(job), input, sourcePaths, cancellationToken);
    }

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
