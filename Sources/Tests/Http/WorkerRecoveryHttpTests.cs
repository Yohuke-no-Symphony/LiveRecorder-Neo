using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>通过真实 TCP 控制和 HTTP 传输验证恢复凭据轮换与成品上传会话。</summary>
public sealed class WorkerRecoveryHttpTests
{
    /// <summary>切片节点恢复后续传原偏移，旧凭据和旧成品会话不能污染新上传。</summary>
    [MongoHttpFact]
    public Task ClippingRecoveryFencesHttpTransfers() => RunAsync(true);

    /// <summary>转码节点使用相同恢复协议，重新生成成品时清理本代次旧断点。</summary>
    [MongoHttpFact]
    public Task TranscodingRecoveryFencesHttpTransfers() => RunAsync(false);

    private static async Task RunAsync(bool clipping)
    {
        using var certificate = new TranscodingTestCertificate();
        await using var fixture = await HttpServerFixture.CreateAsync("Production", "--TranscodingControl:Enabled", "true",
            "--TranscodingControl:ListenAddress", "127.0.0.1", "--TranscodingControl:Port", certificate.ControlPort.ToString(),
            "--TranscodingControl:UseTls", "false");
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        foreach (var service in fixture.App.Services.GetServices<IHostedService>().Where(service => service is MediaJobWorker or RecordingSupervisor))
        {
            await service.StopAsync(CancellationToken.None);
        }
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 3);
        var kind = clipping ? "Clipping" : "Transcoding";
        var worker = new TranscodeWorker
        {
            Kind = kind, Name = "recovery-http", SecretHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("worker-secret"u8)),
            Configuration = new() { TempDirectory = Path.Combine(fixture.DirectoryPath, "worker"), MinimumFreeBytes = 0 },
            Capabilities = new()
            {
                ClippingVersion = clipping ? 1 : 0, HlsPackagingVersion = 1,
                Encoders = clipping ? [] : [new() { Encoder = "h264_nvenc", Available = true }]
            }
        };
        await fixture.Context.Collection<TranscodeWorker>("transcodeWorkers").InsertOneAsync(worker);
        var options = new WorkerConnectionOptions
        {
            ApiBaseUrl = fixture.Address.AbsoluteUri, WorkerId = worker.Id, Secret = "worker-secret",
            ControlHost = "127.0.0.1", ControlPort = certificate.ControlPort, ControlUseTls = false
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var connection = await WorkerControlClient.ConnectAsync(options, timeout.Token);
        var welcome = await connection.ExchangeAsync<WorkerHello, WorkerWelcome>("Hello", "Welcome", new()
        {
            WorkerId = worker.Id, Secret = options.Secret, Kind = kind, Capabilities = worker.Capabilities
        }, timeout.Token);
        Assert.Equal(1, welcome.RecoveryProtocolVersion);
        using (var queued = clipping
            ? await fixture.Admin.SendAsync(HttpMethod.Post, "/api/jobs", new { kind = "Clip", mode = "Exact", sessionId = session.Id, startMs = 0, endMs = 1000 })
            : await fixture.Admin.SendAsync(HttpMethod.Post, "/api/transcoding/jobs", new { sessionIds = new[] { session.Id }, force = false }))
        {
            Assert.True(queued.IsSuccessStatusCode);
        }
        var heartbeat = new WorkerHeartbeat { AppliedConfigurationRevision = welcome.ConfigurationRevision, Telemetry = new() { FreeBytes = long.MaxValue } };
        WorkerState state;
        do
        {
            state = await connection.ExchangeAsync<WorkerHeartbeat, WorkerState>("Heartbeat", "State", heartbeat, timeout.Token);
            if (state.Assignments.Count == 0 && state.ClipAssignments.Count == 0)
            {
                await Task.Delay(25, timeout.Token);
            }
        }
        while (state.Assignments.Count == 0 && state.ClipAssignments.Count == 0);
        var jobId = clipping ? state.ClipAssignments.Single().JobId : state.Assignments.Single().JobId;
        var attempt = clipping ? state.ClipAssignments.Single().Attempt : state.Assignments.Single().Attempt;
        var token = clipping ? state.ClipAssignments.Single().LeaseToken : state.Assignments.Single().LeaseToken;
        var root = "/api/worker/" + (clipping ? "clipping" : "transcoding") + "/jobs/" + jobId;
        var output = root + (clipping ? "/output" : "/artifacts/init.mp4");
        using (var began = await SendAsync(fixture, worker.Id, attempt, token, HttpMethod.Post, root + "/upload-session",
                   JsonContent.Create(new WorkerUploadSession { ManifestFingerprint = new('a', 64) })))
        {
            Assert.True(began.StatusCode == HttpStatusCode.NoContent, await began.Content.ReadAsStringAsync());
        }
        using (var uploaded = await SendAsync(fixture, worker.Id, attempt, token, HttpMethod.Put, output,
                   new ByteArrayContent([1, 2, 3, 4]), 20, 0))
        {
            Assert.Equal(HttpStatusCode.NoContent, uploaded.StatusCode);
            Assert.Equal("4", uploaded.Headers.GetValues("Upload-Offset").Single());
        }
        if (clipping)
        {
            await fixture.Context.Collection<MediaJob>("jobs").UpdateOneAsync(job => job.Id == jobId,
                Builders<MediaJob>.Update.Set(job => job.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(-1)));
        }
        else
        {
            await fixture.Context.Collection<TranscodeJob>("transcodeJobs").UpdateOneAsync(job => job.Id == jobId,
                Builders<TranscodeJob>.Update.Set(job => job.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(-1)));
        }
        var recovery = new WorkerJobRecoveryRequest { JobId = jobId, Attempt = attempt, LeaseToken = token, NextLeaseToken = new('b', 64) };
        heartbeat.RecoveryRequests = [recovery];
        var recovered = await connection.ExchangeAsync<WorkerHeartbeat, WorkerState>("Heartbeat", "State", heartbeat, timeout.Token);
        Assert.Equal("Resume", Assert.Single(recovered.RecoveryResults).Disposition);
        var repeated = await connection.ExchangeAsync<WorkerHeartbeat, WorkerState>("Heartbeat", "State", heartbeat, timeout.Token);
        Assert.Equal("Resume", Assert.Single(repeated.RecoveryResults).Disposition);
        using (var old = await SendAsync(fixture, worker.Id, attempt, token, HttpMethod.Head, output, length: 20))
        {
            Assert.Equal(HttpStatusCode.Conflict, old.StatusCode);
        }
        using (var resumed = await SendAsync(fixture, worker.Id, attempt, recovery.NextLeaseToken, HttpMethod.Head, output, length: 20))
        {
            Assert.Equal(HttpStatusCode.NoContent, resumed.StatusCode);
            Assert.Equal("4", resumed.Headers.GetValues("Upload-Offset").Single());
        }
        using (var rebuilt = await SendAsync(fixture, worker.Id, attempt, recovery.NextLeaseToken, HttpMethod.Post, root + "/upload-session",
                   JsonContent.Create(new WorkerUploadSession { ManifestFingerprint = new('c', 64) })))
        {
            Assert.Equal(HttpStatusCode.NoContent, rebuilt.StatusCode);
        }
        using var empty = await SendAsync(fixture, worker.Id, attempt, recovery.NextLeaseToken, HttpMethod.Head, output, length: 30);
        Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);
        Assert.Equal("0", empty.Headers.GetValues("Upload-Offset").Single());
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpServerFixture fixture, string workerId, int attempt, string token,
        HttpMethod method, string url, HttpContent? content = null, long? length = null, long? offset = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "worker-secret");
        request.Headers.Add("X-Worker-Id", workerId);
        request.Headers.Add("X-Worker-Attempt", attempt.ToString());
        request.Headers.Add("X-Worker-Lease", token);
        request.Headers.Add("X-Transcode-Attempt", attempt.ToString());
        request.Headers.Add("X-Transcode-Lease", token);
        if (length is not null)
        {
            request.Headers.Add("Upload-Length", length.Value.ToString());
        }
        if (offset is not null)
        {
            request.Headers.Add("Upload-Offset", offset.Value.ToString());
        }
        return await fixture.Admin.Client.SendAsync(request);
    }
}
