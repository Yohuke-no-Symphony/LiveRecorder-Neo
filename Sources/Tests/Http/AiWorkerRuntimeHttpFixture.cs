using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.AI.Worker;
using Yohuke.LiveRecorder.AI.Worker.Storage;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class AiWorkerRuntimeHttpFixture : IAsyncDisposable
{
    private readonly TranscodingTestCertificate control = new();
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(45));
    private readonly ConcurrentQueue<string> logs = new();
    private HttpServerFixture? server;
    private WorkerSettingsStore? settings;
    private Task? running;
    private int truncatedSources;
    private bool truncateFirstSource = true;

    private HttpServerFixture Server => server ?? throw new InvalidOperationException("The AI runtime fixture has not been initialized.");
    private AiSettings AiSettings { get; set; } = new();
    private WorkerConnectionOptions Options { get; set; } = new();
    private string NodeDirectory { get; set; } = "";
    internal string WorkerId => Options.WorkerId;
    internal long OriginalSourceLength { get; private set; }
    internal long DeliveredSourceLength { get; private set; }
    internal ConcurrentQueue<JsonObject> ProviderRequests { get; } = new();
    internal ConcurrentQueue<(AiExecutionResult Result, HttpStatusCode StatusCode, string Body)> Completions { get; } = new();
    internal Dictionary<string, byte[]> ProtectedCacheFiles { get; } = new(StringComparer.Ordinal);
    internal string CompletedCacheDirectory { get; private set; } = "";

    internal static async Task<AiWorkerRuntimeHttpFixture> CreateAsync()
    {
        var fixture = new AiWorkerRuntimeHttpFixture();
        try
        {
            await fixture.InitializeAsync();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private async Task InitializeAsync()
    {
        server = await HttpServerFixture.CreateAsync("Production",
            "--TranscodingControl:Enabled", "true", "--TranscodingControl:ListenAddress", "127.0.0.1",
            "--TranscodingControl:Port", control.ControlPort.ToString(), "--TranscodingControl:UseTls", "false");
        await Server.InstallAsync();
        await Server.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var global = (await Server.Admin.Client.GetFromJsonAsync<ServerSettings>("/api/settings", JsonFiles.Options, lifetime.Token))!;
        AiSettings = new()
        {
            Enabled = true, MaxConcurrentJobs = 1, Provider = "Custom", BaseUrl = "http://ai-worker-provider.test/v1",
            Model = "ai-worker-preparation-regression", ApiKey = "ai-worker-test-only-provider-key"
        };
        global.Ai = AiSettings;
        using (var configured = await Server.Admin.SendAsync(HttpMethod.Put, "/api/settings", global))
        {
            Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        }
        using var created = await Server.Admin.SendAsync(HttpMethod.Post, "/api/transcoding/workers",
            new { name = "AI preparation regression", kind = "AI" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var registration = (await created.Content.ReadFromJsonAsync<JsonObject>(lifetime.Token))!;
        Options = new()
        {
            ApiBaseUrl = Server.Address.AbsoluteUri, ControlHost = "127.0.0.1", ControlPort = control.ControlPort,
            ControlUseTls = false, WorkerId = registration["worker"]!["id"]!.GetValue<string>(),
            Secret = registration["secret"]!.GetValue<string>()
        };
        NodeDirectory = Path.Combine(Server.DirectoryPath, "ai-node");
        var configuration = new WorkerConfiguration
        {
            TempDirectory = NodeDirectory, MinimumFreeBytes = 0, MaxCacheBytes = 16L * 1024 * 1024,
            MaxConcurrentEncodes = 1
        };
        using (var configured = await Server.Admin.SendAsync(HttpMethod.Put, "/api/transcoding/workers/" + WorkerId,
            new { name = "AI preparation regression", enabled = true, configuration, expectedRevision = 1 }))
        {
            Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        }
        settings = new(Path.Combine(Server.DirectoryPath, "ai-node-settings.json"), "AI");
        await settings.SaveAsync(new() { Worker = Options, Configuration = configuration }, lifetime.Token);
    }

    internal async Task<AiAnalysisJobSummary> QueueAnalysisAsync(bool withEvidence = true)
    {
        var start = DateTime.UtcNow.AddMinutes(-2);
        var session = new BroadcastSession
        {
            Title = "AI Worker 准备失败回归", DirectoryPath = "ai-runtime-" + Guid.NewGuid().ToString("N"),
            StartedAtUtc = start, EndedAtUtc = start.AddMinutes(1), State = "Completed", HasDanmu = true
        };
        await Server.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(session, cancellationToken: lifetime.Token);
        await Server.Context.Collection<RecordingSegment>("segments").InsertOneAsync(new()
        {
            SessionId = session.Id, RelativePath = "recording.flv", DurationMs = 60000, CommittedBytes = 64, IsClosed = true
        }, cancellationToken: lifetime.Token);
        await using (var writer = new SessionEventWriter(Path.Combine(Server.StoragePath, session.DirectoryPath, "弹幕")))
        {
            if (withEvidence)
            {
                await writer.WriteAsync(new() { OffsetMs = 1000, Text = "【准备阶段应先下载完整的固定证据】" });
            }
        }
        using var queued = await Server.Admin.SendAsync(HttpMethod.Post, $"/api/sessions/{session.Id}/ai-analysis", new { force = false });
        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        return (await queued.Content.ReadFromJsonAsync<AiAnalysisJobSummary>(lifetime.Token))!;
    }

    internal async Task<AiAnalysisMemory> SeedMemoryAsync(AiAnalysisJobSummary job)
    {
        var memory = new AiAnalysisMemory
        {
            JobId = job.Id, SessionId = job.SessionId, Version = 7,
            ConversationJson = """[{"role":"assistant","content":"已保存的上下文需要完整保留"}]"""
        };
        var relative = Path.Combine("ai", "contexts", job.Id, "existing-memory.json");
        await JsonFiles.WriteAsync(Path.Combine(Server.DataDirectory, relative), memory, lifetime.Token);
        await Server.Context.Collection<AiAnalysisJob>("ai_analysis_jobs").UpdateOneAsync(value => value.Id == job.Id,
            Builders<AiAnalysisJob>.Update.Set(value => value.Context.ConversationPath, relative)
                .Set(value => value.Context.ConversationVersion, memory.Version), cancellationToken: lifetime.Token);
        return memory;
    }

    internal void SeedBlockedCache()
    {
        var workspace = new AiWorkspace(settings!.Path, WorkerId, logs.Enqueue);
        var configuration = WorkerConfigurationProjection.ToAi(settings.Current.Configuration);
        var root = workspace.RegisterRoot(NodeDirectory);
        var corruptDirectory = Path.Combine(root, "jobs", "corrupt");
        Directory.CreateDirectory(corruptDirectory);
        File.WriteAllText(Path.Combine(corruptDirectory, "checkpoint.json"), "{broken");
        var completed = workspace.Open(configuration, new() { JobId = "cached-completed", Attempt = 1 });
        completed.Update(value => value.State = "Completed");
        CompletedCacheDirectory = Path.GetDirectoryName(completed.Path)!;
        File.WriteAllBytes(Path.Combine(CompletedCacheDirectory, "completed-input"), new byte[1024 * 1024]);
        var paused = workspace.Open(configuration, new() { JobId = "cached-paused", Attempt = 1, LeaseToken = "paused-lease" });
        paused.Update(value => value.State = "Paused");
        var pausedDirectory = Path.GetDirectoryName(paused.Path)!;
        File.WriteAllBytes(Path.Combine(pausedDirectory, "paused-input"), new byte[1024 * 1024]);
        var unknown = workspace.Open(configuration, new() { JobId = "cached-unknown", Attempt = 1, LeaseToken = "unknown-lease" });
        unknown.Update(value =>
        {
            value.State = "Paused";
            value.Execution.ModelRequests = 1;
            value.Execution.RequestInFlight = true;
            value.Journal = new() { RequestNumber = 1, Dispatched = true, RequestFingerprint = "unknown-response" };
        });
        var unknownDirectory = Path.GetDirectoryName(unknown.Path)!;
        File.WriteAllBytes(Path.Combine(unknownDirectory, "unknown-input"), new byte[1024 * 1024]);
        var orderedDirectories = new[] { corruptDirectory, CompletedCacheDirectory, pausedDirectory, unknownDirectory };
        for (var index = 0; index < orderedDirectories.Length; index++)
        {
            Directory.SetLastWriteTimeUtc(orderedDirectories[index], new DateTime(2000 + index, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }
        foreach (var directory in new[] { corruptDirectory, pausedDirectory, unknownDirectory })
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                ProtectedCacheFiles.Add(path, File.ReadAllBytes(path));
            }
        }
        var pending = workspace.ReadPending();
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, value => value.Read().Assignment.JobId == "cached-paused");
        Assert.Contains(pending, value => value.Read().Journal is { Dispatched: true, HasKnownResponse: false });
    }

    internal async Task<int> SetCacheBudgetAsync(long maxCacheBytes)
    {
        var worker = await ReadWorkerAsync();
        var configuration = worker.Configuration with { MaxCacheBytes = maxCacheBytes };
        using var response = await Server.Admin.SendAsync(HttpMethod.Put, "/api/transcoding/workers/" + WorkerId,
            new { name = worker.Name, enabled = true, configuration, expectedRevision = worker.ConfigurationRevision });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>(lifetime.Token))!["configurationRevision"]!.GetValue<int>();
    }

    internal void StartWorker(bool truncateSource = true)
    {
        truncateFirstSource = truncateSource;
        running = new AiWorkerRuntime(Options, settings!, logs.Enqueue,
            () => new AiWorkerTestHttpHandler(observe: ObserveApiAsync),
            () => new AiWorkerTestHttpHandler(respond: RespondProviderAsync)).RunAsync(lifetime.Token);
    }

    internal Task<AiAnalysisJob> ReadJobAsync(string jobId) => Server.Context.Collection<AiAnalysisJob>("ai_analysis_jobs")
        .Find(value => value.Id == jobId).SingleAsync(lifetime.Token);

    internal Task<TranscodeWorker> ReadWorkerAsync() => Server.Context.Collection<TranscodeWorker>("transcodeWorkers")
        .Find(value => value.Id == WorkerId).SingleAsync(lifetime.Token);

    internal AiWorkerCheckpoint? ReadCheckpoint(string jobId)
    {
        if (!Directory.Exists(NodeDirectory))
        {
            return null;
        }
        foreach (var path in Directory.EnumerateFiles(NodeDirectory, "checkpoint.json", SearchOption.AllDirectories))
        {
            var checkpoint = WorkerJsonFileStore.Read<AiWorkerCheckpoint>(path);
            if (checkpoint.Assignment.JobId == jobId)
            {
                return checkpoint;
            }
        }
        return null;
    }

    internal async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        try
        {
            while (!await condition())
            {
                if (running!.IsCompleted)
                {
                    await running;
                    Assert.Fail("AI runtime stopped before the expected state.\n" + string.Join('\n', logs));
                }
                await Task.Delay(50, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            Assert.Fail("AI runtime did not reach the expected state before the timeout.\n" + string.Join('\n', logs));
        }
    }

    internal async Task<AiConnectionTestResult> TestConnectionAsync()
    {
        using var response = await Server.Admin.SendAsync(HttpMethod.Post, "/api/settings/ai/test", AiSettings);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AiConnectionTestResult>(lifetime.Token))!;
    }

    private async Task ObserveApiAsync(HttpRequestMessage request, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (truncateFirstSource && path.Contains("/sources/", StringComparison.Ordinal) && response.IsSuccessStatusCode
            && Interlocked.CompareExchange(ref truncatedSources, 1, 0) == 0)
        {
            var original = response.Content;
            var bytes = await original.ReadAsByteArrayAsync(cancellationToken);
            Assert.True(bytes.Length > 1);
            OriginalSourceLength = original.Headers.ContentLength ?? bytes.Length;
            DeliveredSourceLength = bytes.Length - 1;
            response.Content = new ByteArrayContent(bytes[..^1]);
            response.Content.Headers.ContentLength = OriginalSourceLength;
            response.Content.Headers.ContentType = original.Headers.ContentType;
            original.Dispose();
        }
        if (path.EndsWith("/complete", StringComparison.Ordinal))
        {
            var payload = JsonSerializer.Deserialize<AiExecutionResult>(await request.Content!.ReadAsByteArrayAsync(cancellationToken), WorkerProtocol.JsonOptions)!;
            Completions.Enqueue((payload, response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken)));
        }
    }

    private async Task<HttpResponseMessage> RespondProviderAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ProviderRequests.Enqueue((await request.Content!.ReadFromJsonAsync<JsonObject>(cancellationToken))!);
        return new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            role = "assistant",
                            tool_calls = new[]
                            {
                                new { id = "connection-test", type = "function", function = new { name = "connection_test", arguments = "{}" } }
                            }
                        },
                        finish_reason = "tool_calls"
                    }
                },
                usage = new { prompt_tokens = 12, completion_tokens = 8, total_tokens = 20 }
            })
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        try
        {
            if (running is not null)
            {
                await running.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            settings?.Dispose();
            lifetime.Dispose();
            try
            {
                if (server is not null)
                {
                    await server.DisposeAsync();
                }
            }
            finally
            {
                control.Dispose();
            }
        }
    }
}
