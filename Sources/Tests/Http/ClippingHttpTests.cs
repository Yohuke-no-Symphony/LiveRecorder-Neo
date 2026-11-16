using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Clipping;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Media;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>Exercises real clipping source ranges, resumable MP4 uploads and existing-library publication over HTTP.</summary>
public sealed class ClippingHttpTests
{
    /// <summary>A small middle range becomes a verified downloadable clip and completion remains idempotent.</summary>
    [MongoHttpFact]
    public async Task PartialSourceUploadAndPublicationRoundTrip()
    {
        await using var fixture = await PlaybackHttpMediaFixture.CreateAsync();
        await fixture.App.Services.GetServices<IHostedService>().OfType<MediaJobWorker>().Single().StopAsync(CancellationToken.None);
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 8);
        var (id, secret, assignment) = await AssignAsync(fixture, session.Id);
        var span = Assert.Single(assignment.Inputs);
        var range = span.Range!;
        Assert.True(range.EndOffset - range.StartOffset < range.SourceLength / 2);
        using var worker = WorkerClient(fixture, id, secret, assignment);
        var url = "/api/worker/clipping/jobs/" + assignment.JobId;
        using var forbiddenWhole = await worker.GetAsync(url + "/inputs/" + span.Id);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, forbiddenWhole.StatusCode);
        var midpoint = range.StartOffset + (range.EndOffset - range.StartOffset) / 2;
        var raw = new List<byte>();
        foreach (var bounds in new[] { (range.StartOffset, midpoint - 1), (midpoint, range.EndOffset - 1) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url + "/inputs/" + span.Id);
            request.Headers.Range = new(bounds.Item1, bounds.Item2);
            request.Headers.IfRange = new(new EntityTagHeaderValue(span.SourceETag));
            using var response = await worker.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(bounds.Item1, response.Content.Headers.ContentRange!.From);
            Assert.Equal(bounds.Item2, response.Content.Headers.ContentRange.To);
            Assert.Equal(range.SourceLength, response.Content.Headers.ContentRange.Length);
            Assert.Equal(span.SourceETag, response.Headers.ETag!.Tag);
            raw.AddRange(await response.Content.ReadAsByteArrayAsync());
        }
        var rebuilt = Path.Combine(fixture.DirectoryPath, "remote-range.flv");
        await using (var input = new MemoryStream(raw.ToArray()))
        await using (var output = File.Create(rebuilt))
        {
            await IndexedFlvRange.CopyAsync(input, new()
            {
                SourceLength = range.SourceLength, StartOffset = range.StartOffset, EndOffset = range.EndOffset,
                StartMs = range.StartMs, EndMs = range.EndMs, VideoHeader = range.VideoHeader, AudioHeader = range.AudioHeader
            }, output);
        }
        var outputPath = Path.Combine(fixture.DirectoryPath, "remote-output.mp4");
        await fixture.App.Services.GetRequiredService<IMediaProcessor>().ExportAsync(new()
        {
            OutputPath = outputPath, StartMs = assignment.StartMs, DurationMs = assignment.EndMs - assignment.StartMs,
            Inputs = [new() { Path = rebuilt, SessionStartMs = span.SessionStartMs, SourceStartMs = span.SourceStartMs - range.StartMs, DurationMs = span.DurationMs }]
        }, new Progress<double>(), CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(outputPath);
        var split = bytes.Length / 2;
        await UploadAsync(worker, url, bytes[..split], 0, bytes.Length);
        using (var query = new HttpRequestMessage(HttpMethod.Head, url + "/output"))
        {
            query.Headers.Add("Upload-Length", bytes.Length.ToString());
            using var response = await worker.SendAsync(query);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(split.ToString(), Assert.Single(response.Headers.GetValues("Upload-Offset")));
        }
        await UploadAsync(worker, url, bytes[split..], split, bytes.Length);
        var manifest = new ClipOutputManifest
        {
            Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            ActualStartMs = assignment.ActualStartMs, ActualEndMs = assignment.ActualEndMs
        };
        using var invalid = await worker.PostAsJsonAsync(url + "/complete", manifest with { ActualEndMs = manifest.ActualEndMs + 1000 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        using var complete = await worker.PostAsJsonAsync(url + "/complete", manifest);
        Assert.True(complete.StatusCode == HttpStatusCode.NoContent, await complete.Content.ReadAsStringAsync());
        using var repeated = await worker.PostAsJsonAsync(url + "/complete", manifest);
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        var job = await fixture.Context.Collection<MediaJob>("jobs").Find(value => value.Id == assignment.JobId).SingleAsync();
        Assert.Equal("Completed", job.State);
        var clip = await fixture.Context.Collection<Clip>("clips").Find(value => value.JobId == assignment.JobId).SingleAsync();
        Assert.Equal(assignment.ActualStartMs, clip.StartMs);
        Assert.Equal(assignment.ActualEndMs, clip.EndMs);
        using var download = await fixture.Admin.Client.GetAsync("/api/clips/" + clip.Id + "/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        Assert.False(Directory.Exists(Path.Combine(fixture.StoragePath, ".work", "jobs", job.Id, assignment.Attempt.ToString())));
    }

    /// <summary>Shared administration preserves Worker type and cancellation immediately revokes clipping transfer.</summary>
    [MongoHttpFact]
    public async Task WorkerKindAndCancellationAreEnforcedAcrossHttpBoundary()
    {
        await using var fixture = await PlaybackHttpMediaFixture.CreateAsync();
        await fixture.App.Services.GetServices<IHostedService>().OfType<MediaJobWorker>().Single().StopAsync(CancellationToken.None);
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 8);
        var (id, secret, assignment) = await AssignAsync(fixture, session.Id);
        var workers = await fixture.Admin.Client.GetFromJsonAsync<JsonArray>("/api/transcoding/workers");
        Assert.Equal("Clipping", workers!.Single(value => value!["id"]!.GetValue<string>() == id)!["kind"]!.GetValue<string>());
        using var worker = WorkerClient(fixture, id, secret, assignment);
        using var cancelled = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/jobs/" + assignment.JobId + "/cancel");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/worker/clipping/jobs/" + assignment.JobId + "/inputs/" + assignment.Inputs[0].Id);
        request.Headers.Range = new(assignment.Inputs[0].Range!.StartOffset, assignment.Inputs[0].Range!.EndOffset - 1);
        using var rejected = await worker.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
    }

    private static async Task<(string Id, string Secret, ClipAssignment Assignment)> AssignAsync(HttpServerFixture fixture, string sessionId)
    {
        using var created = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/transcoding/workers", new { name = "HTTP clipping", kind = "Clipping" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var json = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        var id = json["worker"]!["id"]!.GetValue<string>();
        var secret = json["secret"]!.GetValue<string>();
        using var queued = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/jobs", new
        {
            kind = "Clip", mode = "Exact", sessionId, startMs = 3500, endMs = 4250, title = "Remote clip"
        });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var coordinator = fixture.App.Services.GetRequiredService<TranscodingCoordinator>();
        var welcome = await coordinator.ConnectAsync(new()
        {
            WorkerId = id, Secret = secret, Kind = "Clipping", ExpectedConfigurationRevision = 1,
            InitialConfiguration = new() { TempDirectory = Path.Combine(fixture.DirectoryPath, "clip-worker"), MinimumFreeBytes = 0 },
            Capabilities = new() { ClippingVersion = 1 }
        });
        var state = await coordinator.HeartbeatAsync(id, new()
        {
            AppliedConfigurationRevision = welcome.ConfigurationRevision, Telemetry = new() { FreeBytes = long.MaxValue }
        });
        if (state.ClipAssignments.Count == 0)
        {
            var responseJob = (await queued.Content.ReadFromJsonAsync<MediaJob>())!;
            var job = await fixture.Context.Collection<MediaJob>("jobs").Find(value => value.Id == responseJob.Id).SingleAsync();
            await fixture.App.Services.GetRequiredService<ClipPlanPreparation>().WaitForReadyAsync(job).WaitAsync(TimeSpan.FromSeconds(10));
            state = await coordinator.HeartbeatAsync(id, new()
            {
                AppliedConfigurationRevision = welcome.ConfigurationRevision, Telemetry = new() { FreeBytes = long.MaxValue }
            });
        }
        Assert.Empty(state.Assignments);
        return (id, secret, Assert.Single(state.ClipAssignments));
    }

    private static HttpClient WorkerClient(HttpServerFixture fixture, string id, string secret, ClipAssignment assignment)
    {
        var worker = fixture.NewBrowser().Client;
        worker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        worker.DefaultRequestHeaders.Add("X-Worker-Id", id);
        worker.DefaultRequestHeaders.Add("X-Worker-Attempt", assignment.Attempt.ToString());
        worker.DefaultRequestHeaders.Add("X-Worker-Lease", assignment.LeaseToken);
        return worker;
    }

    private static async Task UploadAsync(HttpClient worker, string url, byte[] bytes, int offset, int total)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url + "/output") { Content = new ByteArrayContent(bytes) };
        request.Headers.Add("Upload-Offset", offset.ToString());
        request.Headers.Add("Upload-Length", total.ToString());
        using var response = await worker.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }
}
