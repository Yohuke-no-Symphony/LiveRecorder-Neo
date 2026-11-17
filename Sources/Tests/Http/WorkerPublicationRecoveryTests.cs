using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>通过真实控制连接验证本地恢复在特殊发布与存储故障下的边界。</summary>
public sealed class WorkerPublicationRecoveryTests
{
    /// <summary>主站已完成但本地清单缺失时，等待归档激活而不重新下载或编码。</summary>
    [Theory]
    [InlineData("Transcoding")]
    [InlineData("Clipping")]
    public async Task CompletedWithoutLocalManifestWaitsForServerActivation(string kind)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var fixture = await WorkerRecoveryRuntimeFixture.CreateAsync(kind, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var lifetime = new CancellationTokenSource();
        var running = fixture.RunAsync(lifetime.Token);
        try
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = connection.GetStream();
            await WelcomeAsync(stream, fixture.Configuration, timeout.Token);
            var first = await ReadHeartbeatAsync(stream, timeout.Token);
            var original = Assert.Single(first.Heartbeat.RecoveryRequests);
            await ReplyAsync(stream, first.Message, fixture.Configuration, new()
            {
                JobId = original.JobId, Attempt = 1, Disposition = "Resume", State = "Completed"
            }, timeout.Token);
            var second = await ReadHeartbeatAsync(stream, timeout.Token);
            var waiting = Assert.Single(second.Heartbeat.RecoveryRequests);
            Assert.Equal(original.NextLeaseToken, waiting.LeaseToken);
            Assert.Empty(second.Heartbeat.Jobs);
            using (var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture.CheckpointPath, timeout.Token)))
            {
                Assert.Equal("AwaitingPublication", saved.RootElement.GetProperty("state").GetString());
                Assert.Empty(saved.RootElement.GetProperty("stages").EnumerateArray());
                Assert.Equal(JsonValueKind.Null, saved.RootElement.GetProperty("payload").GetProperty("manifest").ValueKind);
            }
            await ReplyAsync(stream, second.Message, fixture.Configuration, new()
            {
                JobId = waiting.JobId, Attempt = 1, Disposition = "Completed", State = "Completed"
            }, timeout.Token);
            while (File.Exists(fixture.CheckpointPath))
            {
                await Task.Delay(10, timeout.Token);
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            await IgnoreCancellationAsync(running);
        }
    }

    /// <summary>待认领任务数量受当前并发限制，未发送的检查点保持原样。</summary>
    [Theory]
    [InlineData("Transcoding")]
    [InlineData("Clipping")]
    public async Task RecoveryAdmissionHonorsReducedConcurrency(string kind)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var fixture = await WorkerRecoveryRuntimeFixture.CreateAsync(kind, ((IPEndPoint)listener.LocalEndpoint).Port, jobs: 3);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var lifetime = new CancellationTokenSource();
        var running = fixture.RunAsync(lifetime.Token);
        try
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = connection.GetStream();
            await WelcomeAsync(stream, fixture.Configuration, timeout.Token);
            var heartbeat = (await ReadHeartbeatAsync(stream, timeout.Token)).Heartbeat;
            Assert.Single(heartbeat.RecoveryRequests);
            Assert.Empty(heartbeat.Jobs);
            Assert.Equal(3, Directory.EnumerateFiles(fixture.DirectoryPath, "checkpoint.json", SearchOption.AllDirectories).Count());
        }
        finally
        {
            await lifetime.CancelAsync();
            await IgnoreCancellationAsync(running);
        }
    }

    /// <summary>换租约令牌写盘失败后重连，发送前必须重新落盘。</summary>
    [Theory]
    [InlineData("Transcoding")]
    [InlineData("Clipping")]
    public async Task FailedPendingTokenWriteCannotPublishAnUndurableToken(string kind)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var fixture = await WorkerRecoveryRuntimeFixture.CreateAsync(kind, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var lifetime = new CancellationTokenSource();
        var running = fixture.RunAsync(lifetime.Token);
        var backup = fixture.CheckpointPath + ".backup";
        try
        {
            using (var first = await listener.AcceptTcpClientAsync(timeout.Token))
            {
                var stream = first.GetStream();
                var hello = await WorkerProtocol.ReadAsync(stream, timeout.Token);
                Assert.NotNull(hello);
                File.Move(fixture.CheckpointPath, backup);
                Directory.CreateDirectory(fixture.CheckpointPath);
                await WorkerProtocol.WriteAsync(stream, "Welcome", hello.RequestId, new WorkerWelcome
                {
                    Configuration = fixture.Configuration, ConfigurationRevision = 1, HeartbeatSeconds = 1, RecoveryProtocolVersion = 1
                }, timeout.Token);
                Assert.Null(await WorkerProtocol.ReadAsync(stream, timeout.Token));
            }
            Directory.Delete(fixture.CheckpointPath);
            File.Move(backup, fixture.CheckpointPath);
            using var second = await listener.AcceptTcpClientAsync(timeout.Token);
            var secondStream = second.GetStream();
            await WelcomeAsync(secondStream, fixture.Configuration, timeout.Token);
            var request = Assert.Single((await ReadHeartbeatAsync(secondStream, timeout.Token)).Heartbeat.RecoveryRequests);
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture.CheckpointPath, timeout.Token));
            Assert.Equal(request.NextLeaseToken, document.RootElement.GetProperty("nextLeaseToken").GetString());
            Assert.False(string.IsNullOrWhiteSpace(request.NextLeaseToken));
            second.Close();
            using var third = await listener.AcceptTcpClientAsync(timeout.Token);
            var thirdStream = third.GetStream();
            await WelcomeAsync(thirdStream, fixture.Configuration, timeout.Token);
            var replay = Assert.Single((await ReadHeartbeatAsync(thirdStream, timeout.Token)).Heartbeat.RecoveryRequests);
            Assert.Equal(request.LeaseToken, replay.LeaseToken);
            Assert.Equal(request.NextLeaseToken, replay.NextLeaseToken);
        }
        finally
        {
            if (Directory.Exists(fixture.CheckpointPath))
            {
                Directory.Delete(fixture.CheckpointPath);
            }
            if (File.Exists(backup))
            {
                File.Move(backup, fixture.CheckpointPath, overwrite: true);
            }
            await lifetime.CancelAsync();
            await IgnoreCancellationAsync(running);
        }
    }

    /// <summary>终态补报不占执行恢复名额，补报 HTTP 失败也不能阻塞控制心跳。</summary>
    [Theory]
    [InlineData("Transcoding")]
    [InlineData("Clipping")]
    public async Task PendingResultRecoveryDoesNotConsumeExecutionAdmissionOrBlockHeartbeats(string kind)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var fixture = await WorkerRecoveryRuntimeFixture.CreateAsync(kind, ((IPEndPoint)listener.LocalEndpoint).Port, jobs: 2);
        var pending = JsonNode.Parse(await File.ReadAllTextAsync(fixture.CheckpointPath))!;
        pending["state"] = "Reporting";
        pending["pendingResult"] = JsonSerializer.SerializeToNode(new WorkerJobResult
        {
            State = "Failed", ErrorCode = "WorkerExecutionFailed", Error = "Persisted failure"
        }, WorkerProtocol.JsonOptions);
        await File.WriteAllTextAsync(fixture.CheckpointPath, pending.ToJsonString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var lifetime = new CancellationTokenSource();
        var running = fixture.RunAsync(lifetime.Token);
        try
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = connection.GetStream();
            await WelcomeAsync(stream, fixture.Configuration, timeout.Token);
            var first = await ReadHeartbeatAsync(stream, timeout.Token);
            Assert.Equal(2, first.Heartbeat.RecoveryRequests.Count);
            var report = Assert.Single(first.Heartbeat.RecoveryRequests, value => value.JobId == "job-1");
            await ReplyAsync(stream, first.Message, fixture.Configuration, new()
            {
                JobId = report.JobId, Attempt = report.Attempt, Disposition = "Resume", State = "Running"
            }, timeout.Token);
            var next = await ReadHeartbeatAsync(stream, timeout.Token);
            Assert.Equal("job-1", Assert.Single(next.Heartbeat.Jobs).JobId);
            Assert.Equal("job-0", Assert.Single(next.Heartbeat.RecoveryRequests).JobId);
            using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture.CheckpointPath, timeout.Token));
            Assert.Equal("Reporting", saved.RootElement.GetProperty("state").GetString());
            Assert.Equal("Failed", saved.RootElement.GetProperty("pendingResult").GetProperty("state").GetString());
        }
        finally
        {
            await lifetime.CancelAsync();
            await IgnoreCancellationAsync(running);
        }
    }

    private static async Task WelcomeAsync(NetworkStream stream, WorkerConfiguration configuration, CancellationToken cancellationToken)
    {
        var hello = await WorkerProtocol.ReadAsync(stream, cancellationToken);
        Assert.NotNull(hello);
        await WorkerProtocol.WriteAsync(stream, "Welcome", hello.RequestId, new WorkerWelcome
        {
            Configuration = configuration, ConfigurationRevision = 1, HeartbeatSeconds = 1, RecoveryProtocolVersion = 1
        }, cancellationToken);
    }

    private static async Task<(WorkerControlMessage Message, WorkerHeartbeat Heartbeat)> ReadHeartbeatAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var message = await WorkerProtocol.ReadAsync(stream, cancellationToken);
        Assert.NotNull(message);
        return (message, message.Payload.Deserialize<WorkerHeartbeat>(WorkerProtocol.JsonOptions)!);
    }

    private static Task ReplyAsync(NetworkStream stream, WorkerControlMessage request, WorkerConfiguration configuration,
        WorkerJobRecoveryStatus status, CancellationToken cancellationToken) => WorkerProtocol.WriteAsync(stream, "State", request.RequestId,
        new WorkerState { Configuration = configuration, ConfigurationRevision = 1, RecoveryResults = [status] }, cancellationToken);

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
