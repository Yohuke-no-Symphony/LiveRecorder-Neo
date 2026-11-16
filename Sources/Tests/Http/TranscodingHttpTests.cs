using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Playback;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证独立 Worker 的真实 TLS、HTTP 传输、成品播放及回收边界。</summary>
public sealed class TranscodingHttpTests
{
    /// <summary>每档清晰度优先级兼容旧请求，独立保存并在重启后保留。</summary>
    [MongoHttpFact]
    public async Task ProfilePrioritiesPersistThroughSettingsApiAndRestart()
    {
        await using var fixture = await HttpServerFixture.CreateAsync("Production", "--TranscodingControl:Enabled", "false");
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        var profiles = settings!["transcoding"]!["profiles"]!.AsArray();
        Assert.Equal(0, profiles[0]!["priority"]!.GetValue<int>());
        profiles[0]!.AsObject().Remove("priority");
        profiles.Add(new JsonObject
        {
            ["id"] = "1080p", ["name"] = "1080p", ["height"] = 1080, ["priority"] = 100
        });
        settings["transcoding"]!["qualityOrder"]!.AsArray().Add("1080p");

        using var created = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var saved = await created.Content.ReadFromJsonAsync<JsonObject>();
        var savedProfiles = saved!["transcoding"]!["profiles"]!.AsArray();
        Assert.Equal(0, savedProfiles[0]!["priority"]!.GetValue<int>());
        Assert.Equal(100, savedProfiles[1]!["priority"]!.GetValue<int>());
        savedProfiles[0]!["priority"] = int.MinValue;
        savedProfiles[1]!["priority"] = int.MaxValue;

        using var updated = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", saved);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        await fixture.RestartAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);

        var restored = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        var restoredProfiles = restored!["transcoding"]!["profiles"]!.AsArray();
        Assert.Equal(int.MinValue, restoredProfiles[0]!["priority"]!.GetValue<int>());
        Assert.Equal(int.MaxValue, restoredProfiles[1]!["priority"]!.GetValue<int>());
        Assert.Equal("720p", restored["transcoding"]!["defaultPlaybackQualityId"]!.GetValue<string>());
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigFile));
        Assert.Equal(int.MinValue, persisted!["transcoding"]!["profiles"]![0]!["priority"]!.GetValue<int>());
        Assert.Equal(int.MaxValue, persisted["transcoding"]!["profiles"]![1]!["priority"]!.GetValue<int>());
    }

    /// <summary>每档位自动转码偏好经设置接口独立保存，兼容旧请求并在重启后保留。</summary>
    [MongoHttpFact]
    public async Task ProfileAutomaticTranscodingPreferencesPersistThroughSettingsApiAndRestart()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        var profiles = settings!["transcoding"]!["profiles"]!.AsArray();
        Assert.True(profiles[0]!["autoTranscode"]!.GetValue<bool>());
        profiles[0]!.AsObject().Remove("autoTranscode");
        profiles.Add(new JsonObject
        {
            ["id"] = "1080p",
            ["name"] = "1080p",
            ["height"] = 1080,
            ["enabled"] = true,
            ["autoTranscode"] = false
        });
        settings["transcoding"]!["qualityOrder"]!.AsArray().Add("1080p");

        using var created = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var saved = await created.Content.ReadFromJsonAsync<JsonObject>();
        var savedProfiles = saved!["transcoding"]!["profiles"]!.AsArray();
        Assert.True(savedProfiles[0]!["autoTranscode"]!.GetValue<bool>());
        Assert.False(savedProfiles[1]!["autoTranscode"]!.GetValue<bool>());
        savedProfiles[0]!["autoTranscode"] = false;
        savedProfiles[1]!["autoTranscode"] = true;

        using var updated = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", saved);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        await fixture.RestartAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);

        var restored = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        var restoredProfiles = restored!["transcoding"]!["profiles"]!.AsArray();
        Assert.False(restoredProfiles[0]!["autoTranscode"]!.GetValue<bool>());
        Assert.True(restoredProfiles[1]!["autoTranscode"]!.GetValue<bool>());
        Assert.Equal("720p", restored["transcoding"]!["defaultPlaybackQualityId"]!.GetValue<string>());
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigFile));
        Assert.False(persisted!["transcoding"]!["profiles"]![0]!["autoTranscode"]!.GetValue<bool>());
        Assert.True(persisted["transcoding"]!["profiles"]![1]!["autoTranscode"]!.GetValue<bool>());
    }

    /// <summary>本地完整设置按版本提交，运行中的节点将后台变更同步到同一文件。</summary>
    [MongoHttpFact]
    public async Task WorkerSettingsRoundTripThroughTlsAndPersistLiveAdminChanges()
    {
        using var certificate = new TranscodingTestCertificate();
        await using var fixture = await CreateAsync(certificate, true);
        var created = await CreateWorkerAsync(fixture);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var settings = new WorkerSettingsStore(Path.Combine(fixture.DirectoryPath, "worker-settings.json"));
        var options = new WorkerConnectionOptions
        {
            ApiBaseUrl = fixture.Address.AbsoluteUri,
            ControlHost = "127.0.0.1",
            ControlPort = certificate.ControlPort,
            WorkerId = created.Id,
            Secret = created.Secret,
            ServerCertificateSha256 = certificate.Sha256
        };
        await settings.SaveAsync(new() { Worker = options, Configuration = Configuration(fixture) }, timeout.Token);
        WorkerWelcome welcome;
        await using (var connection = await WorkerControlClient.ConnectAsync(options, timeout.Token))
        {
            welcome = await connection.ExchangeAsync<WorkerHello, WorkerWelcome>("Hello", "Welcome", new()
            {
                WorkerId = options.WorkerId, Secret = options.Secret
            }, timeout.Token);
        }
        var update = WorkerSettingsSynchronization.CreateUpdate(settings.Current, welcome);
        Assert.NotNull(update);
        await using (var connection = await WorkerControlClient.ConnectAsync(options, timeout.Token))
        {
            welcome = await connection.ExchangeAsync<WorkerHello, WorkerWelcome>("Hello", "Welcome", update, timeout.Token);
        }
        await settings.SaveRemoteConfigurationAsync(welcome.Configuration, welcome.ConfigurationRevision, timeout.Token);
        Assert.Equal(2, settings.Current.ConfigurationRevision);
        using var runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var running = new WorkerRuntime(options, settings).RunAsync(runtimeCancellation.Token);
        try
        {
            foreach (var revision in new[] { 3, 4 })
            {
                using var edited = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/transcoding/workers/" + created.Id, new
                {
                    name = "同步设置回归 Worker", enabled = true,
                    configuration = welcome.Configuration with { MaxConcurrentEncodes = revision + 1, GpuWaitSeconds = revision * 4 },
                    expectedRevision = revision - 1
                });
                Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
                while (true)
                {
                    var saved = JsonSerializer.Deserialize<WorkerSettings>(await File.ReadAllTextAsync(settings.Path, timeout.Token))!;
                    if (saved.ConfigurationRevision == revision)
                    {
                        Assert.Equal(revision + 1, saved.Configuration.MaxConcurrentEncodes);
                        Assert.Equal(revision * 4, saved.Configuration.GpuWaitSeconds);
                        Assert.Equal(created.Id, saved.Worker.WorkerId);
                        Assert.Equal(created.Secret, saved.Worker.Secret);
                        break;
                    }
                    await Task.Delay(50, timeout.Token);
                }
            }
        }
        finally
        {
            await runtimeCancellation.CancelAsync();
            try
            {
                await running;
            }
            catch (OperationCanceledException) when (runtimeCancellation.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>Worker 使用独立凭据和配置版本，密钥轮换立即撤销已建立连接。</summary>
    [MongoHttpFact]
    public async Task TlsControlUsesVersionedConfigurationAndRevocableCredentials()
    {
        using var certificate = new TranscodingTestCertificate();
        await using var fixture = await CreateAsync(certificate, true);
        var created = await CreateWorkerAsync(fixture);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, certificate.ControlPort);
        await using var tls = new SslStream(client.GetStream(), false,
            (_, value, _, _) => value?.GetCertHashString(HashAlgorithmName.SHA256) == certificate.Sha256);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, timeout.Token);
        await WorkerProtocol.WriteAsync(tls, "Hello", "hello", new WorkerHello
        {
            WorkerId = created.Id, Secret = created.Secret, Capabilities = Capabilities(),
            InitialConfiguration = Configuration(fixture), ExpectedConfigurationRevision = 1
        }, timeout.Token);
        var message = await WorkerProtocol.ReadAsync(tls, timeout.Token);
        Assert.Equal("Welcome", message?.Type);
        var welcome = message!.Payload.Deserialize<WorkerWelcome>(WorkerProtocol.JsonOptions)!;
        Assert.Equal(2, welcome.ConfigurationRevision);
        using var staleClient = new TcpClient();
        await staleClient.ConnectAsync(IPAddress.Loopback, certificate.ControlPort);
        await using var staleTls = new SslStream(staleClient.GetStream(), false,
            (_, value, _, _) => value?.GetCertHashString(HashAlgorithmName.SHA256) == certificate.Sha256);
        await staleTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, timeout.Token);
        await WorkerProtocol.WriteAsync(staleTls, "Hello", "stale", new WorkerHello
        {
            WorkerId = created.Id, Secret = created.Secret, Capabilities = Capabilities(),
            InitialConfiguration = Configuration(fixture), ExpectedConfigurationRevision = 1
        }, timeout.Token);
        var staleMessage = await WorkerProtocol.ReadAsync(staleTls, timeout.Token);
        Assert.Equal("Error", staleMessage?.Type);
        Assert.Equal(409, staleMessage!.Payload.GetProperty("statusCode").GetInt32());
        using var stale = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/transcoding/workers/" + created.Id,
            new { name = "旧配置", enabled = true, configuration = Configuration(fixture), expectedRevision = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var list = await fixture.Admin.Client.GetStringAsync("/api/transcoding/workers");
        Assert.DoesNotContain(created.Secret, list);
        Assert.DoesNotContain("secretHash", list);
        using var rotated = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/transcoding/workers/" + created.Id + "/rotate-key");
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        await WorkerProtocol.WriteAsync(tls, "Heartbeat", "revoked", new WorkerHeartbeat { AppliedConfigurationRevision = 2 }, timeout.Token);
        message = await WorkerProtocol.ReadAsync(tls, timeout.Token);
        Assert.Equal("Error", message?.Type);
        Assert.Equal(401, message!.Payload.GetProperty("statusCode").GetInt32());
    }

    /// <summary>范围读取和断点上传只在当前租约有效时工作，浏览器 Cookie 不能代替 Worker 凭据。</summary>
    [MongoHttpFact]
    public async Task TransfersResumeAtCommittedOffsetsAndStopAfterCancellation()
    {
        using var certificate = new TranscodingTestCertificate();
        await using var fixture = await CreateAsync(certificate);
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 8);
        var created = await CreateWorkerAsync(fixture);
        var assignment = await AssignAsync(fixture, session.Id, created.Id, created.Secret);
        using var unauthorized = await fixture.Admin.Client.GetAsync("/api/worker/transcoding/jobs/" + assignment.JobId + "/input");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var worker = WorkerClient(fixture, created.Id, created.Secret, assignment);
        using var range = new HttpRequestMessage(HttpMethod.Get, Input(assignment));
        range.Headers.Range = new(0, 15);
        using var response = await worker.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(16, (await response.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal(assignment.SourceLength, response.Content.Headers.ContentRange!.Length);
        Assert.Equal(assignment.SourceETag, response.Headers.ETag!.ToString());
        await UploadAsync(worker, assignment, "chunk_00000.m4s", [1, 2, 3, 4], 0, 10);
        using var head = new HttpRequestMessage(HttpMethod.Head, Artifact(assignment, "chunk_00000.m4s"));
        head.Headers.Add("Upload-Length", "10");
        using var offset = await worker.SendAsync(head);
        Assert.Equal("4", Assert.Single(offset.Headers.GetValues("Upload-Offset")));
        using var conflicting = new HttpRequestMessage(HttpMethod.Put, Artifact(assignment, "chunk_00000.m4s"))
        {
            Content = new ByteArrayContent([9, 9])
        };
        conflicting.Headers.Add("Upload-Offset", "0");
        conflicting.Headers.Add("Upload-Length", "10");
        using var conflict = await worker.SendAsync(conflicting);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await UploadAsync(worker, assignment, "chunk_00000.m4s", [5, 6, 7, 8, 9, 10], 4, 10);
        using var cancelled = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/transcoding/jobs/" + assignment.JobId + "/cancel");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        using var denied = await worker.GetAsync(Input(assignment));
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Empty(await fixture.Context.Collection<PlaybackRendition>("playbackRenditions").Find(_ => true).ToListAsync());
    }

    /// <summary>热更新取消执行资源时允许交还任务，旧租约即刻失效并由新尝试重新领取。</summary>
    [MongoHttpFact]
    public async Task WorkerCanReturnPreparedJobWhenConfigurationRemovesCapacity()
    {
        using var certificate = new TranscodingTestCertificate();
        await using var fixture = await CreateAsync(certificate);
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 8);
        var created = await CreateWorkerAsync(fixture);
        var assignment = await AssignAsync(fixture, session.Id, created.Id, created.Secret);
        using var worker = WorkerClient(fixture, created.Id, created.Secret, assignment);
        var url = "/api/worker/transcoding/jobs/" + assignment.JobId + "/result";
        using var invalid = await worker.PostAsJsonAsync(url, new WorkerJobResult { State = "Queued" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var released = await worker.PostAsJsonAsync(url, new WorkerJobResult
        {
            State = "Queued", ErrorCode = "WorkerCapacityUnavailable", Error = "配置不再提供可用编码资源。"
        });
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        using var oldLease = await worker.GetAsync(Input(assignment));
        Assert.Equal(HttpStatusCode.Conflict, oldLease.StatusCode);
        var state = await fixture.App.Services.GetRequiredService<TranscodingCoordinator>().HeartbeatAsync(created.Id, new()
        {
            AppliedConfigurationRevision = 2,
            Telemetry = new() { FreeBytes = long.MaxValue, CpuPercent = 0, CpuThreadBudget = 2 }
        });
        var next = Assert.Single(state.Assignments);
        Assert.Equal(assignment.JobId, next.JobId);
        Assert.True(next.Attempt > assignment.Attempt);
        Assert.NotEqual(assignment.LeaseToken, next.LeaseToken);
    }

    /// <summary>主站验证真实 HLS 后才发布；原片回收还原同时控制成品访问和复用。</summary>
    [MongoHttpFact]
    public async Task PublishedRenditionIsCachedAuthorizedAndRestoredWithRecording()
    {
        using var certificate = new TranscodingTestCertificate();
        await using var fixture = await CreateAsync(certificate);
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 18, bFrames: 2);
        using var original = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/sessions/" + session.Id + "/playback", new { offsetMs = 5000 });
        var fallback = await original.Content.ReadFromJsonAsync<PlaybackResponse>();
        Assert.Equal("64p", fallback!.RequestedQualityId);
        Assert.Equal("original", fallback.EffectiveQualityId);
        var created = await CreateWorkerAsync(fixture);
        var assignment = await AssignAsync(fixture, session.Id, created.Id, created.Secret);
        var coordinator = fixture.App.Services.GetRequiredService<TranscodingCoordinator>();
        var input = await coordinator.GetInputAsync(created.Id, assignment.JobId, assignment.Attempt, assignment.LeaseToken);
        var output = Path.Combine(fixture.DirectoryPath, "encoded");
        Directory.CreateDirectory(output);
        var settings = await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync();
        await RunAsync(settings.FfmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-i", input.Path, "-vf", "scale=-2:64", "-c:v", "libx264",
            "-preset", "medium", "-crf", "30", "-g", "20", "-keyint_min", "20", "-sc_threshold", "0", "-c:a", "copy",
            "-f", "hls", "-hls_time", "6", "-hls_segment_type", "fmp4", "-hls_playlist_type", "vod",
            "-hls_segment_options", "use_editlist=0",
            "-hls_flags", "independent_segments", "-hls_fmp4_init_filename", "init.mp4",
            "-hls_segment_filename", Path.Combine(output, "chunk_%05d.m4s"), Path.Combine(output, "index.m3u8")
        ]);
        var manifest = new TranscodeOutputManifest { Encoder = "libx264" };
        using var worker = WorkerClient(fixture, created.Id, created.Secret, assignment);
        foreach (var file in Directory.EnumerateFiles(output))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            var name = Path.GetFileName(file);
            manifest.Files.Add(new() { Name = name, Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
            await UploadAsync(worker, assignment, name, bytes, 0, bytes.Length);
        }
        using var complete = await worker.PostAsJsonAsync("/api/worker/transcoding/jobs/" + assignment.JobId + "/complete", manifest);
        Assert.True(complete.StatusCode == HttpStatusCode.NoContent, await complete.Content.ReadAsStringAsync());
        using var repeated = await worker.PostAsJsonAsync("/api/worker/transcoding/jobs/" + assignment.JobId + "/complete", manifest);
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        var catalog = fixture.Context.Collection<PlaybackRendition>("playbackRenditions");
        var current = await catalog.Find(x => x.JobId == assignment.JobId).SingleAsync();
        var oldRecipe = JsonSerializer.Deserialize<PlaybackRendition>(JsonSerializer.Serialize(current))!;
        oldRecipe.Id = "late-old-recipe";
        oldRecipe.RecipeHash = "old-recipe";
        oldRecipe.CreatedAtUtc = current.CreatedAtUtc.AddMinutes(1);
        await catalog.InsertOneAsync(oldRecipe);
        using var selected = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/sessions/" + session.Id + "/playback", new { offsetMs = 10000 });
        var playback = await selected.Content.ReadFromJsonAsync<PlaybackResponse>();
        Assert.Equal("64p", playback!.EffectiveQualityId);
        Assert.Equal("HlsInitPts", playback.TimestampMapping);
        Assert.NotNull(playback.MediaStartOffsetMs);
        Assert.NotNull(playback.MediaEndTimestampMs);
        Assert.True(double.IsFinite(playback.StartOffsetMs));
        Assert.Equal(current.Id, playback.ArtifactVersion);
        Assert.Contains("/renditions/", playback.Url);
        using var atStart = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/sessions/" + session.Id + "/playback", new { offsetMs = 0 });
        var firstFrame = await atStart.Content.ReadFromJsonAsync<PlaybackResponse>();
        Assert.Equal("64p", firstFrame!.EffectiveQualityId);
        Assert.Equal(0, firstFrame.MediaStartOffsetMs);
        await fixture.App.Services.GetRequiredService<ISettingsStore>().UpdateAsync(value =>
        {
            value.Transcoding.Profiles[0] = value.Transcoding.Profiles[0] with { VideoBitrateKbps = 32, Name = "已改名" };
            return value;
        });
        using var pendingRecipe = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/sessions/" + session.Id + "/playback", new { offsetMs = 10000 });
        var retained = await pendingRecipe.Content.ReadFromJsonAsync<PlaybackResponse>();
        Assert.Equal(oldRecipe.Id, retained!.ArtifactVersion);
        Assert.Equal("已改名", Assert.Single(retained.Qualities, quality => quality.Id == "64p").Name);
        await fixture.App.Services.GetRequiredService<ISettingsStore>().UpdateAsync(value =>
        {
            value.Transcoding.Profiles = settings.Transcoding.Profiles;
            return value;
        });
        using var playlist = await fixture.Admin.Client.GetAsync(playback.Url);
        Assert.Equal(HttpStatusCode.OK, playlist.StatusCode);
        Assert.True(playlist.Headers.CacheControl!.Private);
        Assert.True(playlist.Headers.CacheControl.NoCache);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, playback.Url);
        conditional.Headers.IfNoneMatch.Add(playlist.Headers.ETag!);
        using var unchanged = await fixture.Admin.Client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        await PlaybackHttpMediaFixture.DownloadAndDecodeAsync(fixture, playback);
        using var anonymous = await fixture.NewBrowser().Client.GetAsync(playback.Url);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var deleted = await fixture.Admin.SendAsync(HttpMethod.Delete, "/api/sessions/" + session.Id);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var removed = await fixture.Admin.Client.GetAsync(playback.Url);
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        var entry = await fixture.Context.Collection<TrashEntry>("trash").Find(x => x.EntityId == session.Id).SingleAsync();
        using var restored = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/trash/" + entry.Id + "/restore");
        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        using var restoredPlayback = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/sessions/" + session.Id + "/playback", new { offsetMs = 10000 });
        var restoredWindow = await restoredPlayback.Content.ReadFromJsonAsync<PlaybackResponse>();
        Assert.Equal(playback.ArtifactVersion, restoredWindow!.ArtifactVersion);
        using var restoredAsset = await fixture.Admin.Client.GetAsync(restoredWindow.Url);
        Assert.Equal(HttpStatusCode.OK, restoredAsset.StatusCode);
    }

    private static async Task<HttpServerFixture> CreateAsync(TranscodingTestCertificate certificate, bool control = false)
    {
        var fixture = await HttpServerFixture.CreateHttpsAsync(certificate.Path, certificate.Sha256,
            "--TranscodingControl:Enabled", control.ToString(), "--TranscodingControl:ListenAddress", "127.0.0.1",
            "--TranscodingControl:UseTls", "true",
            "--TranscodingControl:Port", certificate.ControlPort.ToString(), "--TranscodingControl:CertificatePath", certificate.Path);
        try
        {
            await fixture.InstallAsync();
            await fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single().StopAsync(CancellationToken.None);
            await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
            await fixture.App.Services.GetRequiredService<ISettingsStore>().UpdateAsync(settings =>
            {
                settings.MinimumFreeBytes = 0;
                settings.Transcoding.Profiles = [new() { Id = "64p", Name = "64p", Height = 64 }];
                settings.Transcoding.QualityOrder = ["original", "64p"];
                settings.Transcoding.DefaultPlaybackQualityId = "64p";
                return settings;
            });
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private static async Task<(string Id, string Secret)> CreateWorkerAsync(HttpServerFixture fixture)
    {
        using var response = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/transcoding/workers", new { name = "回归 Worker" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        return (json["worker"]!["id"]!.GetValue<string>(), json["secret"]!.GetValue<string>());
    }

    private static async Task<TranscodeAssignment> AssignAsync(HttpServerFixture fixture, string sessionId, string workerId, string secret)
    {
        using var queued = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/transcoding/jobs", new { sessionIds = new[] { sessionId } });
        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        var coordinator = fixture.App.Services.GetRequiredService<TranscodingCoordinator>();
        var welcome = await coordinator.ConnectAsync(new()
        {
            WorkerId = workerId, Secret = secret, InitialConfiguration = Configuration(fixture),
            ExpectedConfigurationRevision = 1, Capabilities = Capabilities()
        });
        var state = await coordinator.HeartbeatAsync(workerId, new()
        {
            AppliedConfigurationRevision = welcome.ConfigurationRevision,
            Telemetry = new() { FreeBytes = long.MaxValue, CpuPercent = 0, CpuThreadBudget = 2 }
        });
        return Assert.Single(state.Assignments);
    }

    private static WorkerConfiguration Configuration(HttpServerFixture fixture) => new()
    {
        TempDirectory = Path.Combine(fixture.DirectoryPath, "worker"), MinimumFreeBytes = 0
    };

    private static WorkerCapabilities Capabilities() => new()
    {
        OperatingSystem = "test", Encoders = [new() { Encoder = "h264_videotoolbox", Available = true }]
    };

    private static HttpClient WorkerClient(HttpServerFixture fixture, string id, string secret, TranscodeAssignment assignment)
    {
        var client = fixture.NewBrowser().Client;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        client.DefaultRequestHeaders.Add("X-Worker-Id", id);
        client.DefaultRequestHeaders.Add("X-Transcode-Attempt", assignment.Attempt.ToString());
        client.DefaultRequestHeaders.Add("X-Transcode-Lease", assignment.LeaseToken);
        return client;
    }

    private static string Input(TranscodeAssignment assignment) => "/api/worker/transcoding/jobs/" + assignment.JobId + "/input";
    private static string Artifact(TranscodeAssignment assignment, string name) => "/api/worker/transcoding/jobs/" + assignment.JobId + "/artifacts/" + name;

    private static async Task UploadAsync(HttpClient worker, TranscodeAssignment assignment, string name, byte[] bytes, long offset, long length)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Artifact(assignment, name)) { Content = new ByteArrayContent(bytes) };
        request.Headers.Add("Upload-Offset", offset.ToString());
        request.Headers.Add("Upload-Length", length.ToString());
        using var response = await worker.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        Assert.Equal((offset + bytes.Length).ToString(), Assert.Single(response.Headers.GetValues("Upload-Offset")));
    }

    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
