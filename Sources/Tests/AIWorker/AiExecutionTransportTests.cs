using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.AI.Worker.Transport;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.AI.Worker.Tests;

public sealed class AiExecutionTransportTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ai-download-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DownloadsFixedSourceWithWorkerIdentityAndAtomicallyReusesCompleteCache()
    {
        var calls = 0;
        var bytes = Encoding.UTF8.GetBytes("{\"text\":\"正文\"}\n");
        using var handler = new AiHttpMessageHandler((request, _) =>
        {
            calls++;
            Assert.Equal("/api/worker/ai/jobs/job/sources/source", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer binding-secret", request.Headers.Authorization!.ToString());
            Assert.Equal("worker", Assert.Single(request.Headers.GetValues("X-Worker-Id")));
            Assert.Equal("3", Assert.Single(request.Headers.GetValues("X-Ai-Attempt")));
            Assert.Equal("lease", Assert.Single(request.Headers.GetValues("X-Ai-Lease-Token")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        });
        using var transport = new AiExecutionTransport(Options(), handler);
        var source = new AiInputSource { Id = "source", Length = bytes.Length };
        var destination = Path.Combine(directory, "input.jsonl");

        await transport.DownloadAsync(Assignment(), source, destination, 0, default);
        await transport.DownloadAsync(Assignment(), source, destination, 0, default);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.Equal(1, calls);
        Assert.Single(Directory.EnumerateFiles(directory));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(30)]
    public async Task IncompleteOrOversizedSourceNeverBecomesAReusableCache(int expectedLength)
    {
        using var handler = new AiHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("content"))
        }));
        using var transport = new AiExecutionTransport(Options(), handler);
        var path = Path.Combine(directory, "input.jsonl");

        await Assert.ThrowsAsync<InvalidDataException>(() => transport.DownloadAsync(Assignment(), new() { Id = "source", Length = expectedLength }, path, 0, default));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(directory));
    }

    [Fact]
    public async Task CheckpointIsNotAutomaticallyRetriedAndLeaseRejectionIsExplicit()
    {
        var calls = 0;
        using var handler = new AiHttpMessageHandler(async (request, token) =>
        {
            calls++;
            var payload = JsonSerializer.Deserialize<AiExecutionCheckpoint>(await request.Content!.ReadAsStringAsync(token), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal(7, payload!.Sequence);
            Assert.Equal("/api/worker/ai/jobs/job/checkpoint", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        });
        using var transport = new AiExecutionTransport(Options(), handler);

        await Assert.ThrowsAsync<WorkerLeaseLostException>(() => transport.CheckpointAsync(Assignment(), new() { Sequence = 7 }, default));

        Assert.Equal(1, calls);
    }

    private static WorkerConnectionOptions Options() => new() { ApiBaseUrl = "http://coordinator", WorkerId = "worker", Secret = "binding-secret" };
    private static AiAssignment Assignment() => new() { JobId = "job", Attempt = 3, LeaseToken = "lease" };

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
