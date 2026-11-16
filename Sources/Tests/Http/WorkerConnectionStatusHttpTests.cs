using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>Checks independent control-connection status for different Worker processes sharing one host.</summary>
public sealed class WorkerConnectionStatusHttpTests
{
    /// <summary>Stopping one kind immediately disconnects only its own identity and retains the historical heartbeat.</summary>
    [MongoHttpFact]
    public async Task ThreeWorkerKindsOnOneHostDisconnectIndependently()
    {
        using var ports = new TranscodingTestCertificate();
        await using var fixture = await HttpServerFixture.CreateAsync("Production", "--TranscodingControl:Enabled", "true",
            "--TranscodingControl:ListenAddress", "127.0.0.1", "--TranscodingControl:Port", ports.ControlPort.ToString(),
            "--TranscodingControl:UseTls", "false");
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var transcoding = await CreateAsync(fixture, "Transcoding");
        var clipping = await CreateAsync(fixture, "Clipping");
        var recording = await CreateAsync(fixture, "Recording");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var tc = await ConnectAsync(fixture, ports.ControlPort, transcoding, timeout.Token);
        await using var clip = await ConnectAsync(fixture, ports.ControlPort, clipping, timeout.Token);
        await using var rec = await ConnectAsync(fixture, ports.ControlPort, recording, timeout.Token);
        var rows = await ReadAsync(fixture, timeout.Token);
        Assert.True(Online(rows, transcoding.Id));
        Assert.True(Online(rows, clipping.Id));
        Assert.True(Online(rows, recording.Id));
        await clip.DisposeAsync();
        rows = await WaitOfflineAsync(fixture, clipping.Id, timeout.Token);
        Assert.True(Online(rows, transcoding.Id));
        Assert.True(Online(rows, recording.Id));
        Assert.NotNull(rows.Single(row => row["id"]!.GetValue<string>() == clipping.Id)["lastSeenAtUtc"]);
        await rec.DisposeAsync();
        rows = await WaitOfflineAsync(fixture, recording.Id, timeout.Token);
        Assert.True(Online(rows, transcoding.Id));
        Assert.False(Online(rows, clipping.Id));
    }

    /// <summary>An older connection finishing after replacement cannot mark the replacement offline.</summary>
    [MongoHttpFact]
    public async Task ReplacingAConnectionDoesNotInheritItsOldDisconnect()
    {
        using var ports = new TranscodingTestCertificate();
        await using var fixture = await HttpServerFixture.CreateAsync("Production", "--TranscodingControl:Enabled", "true",
            "--TranscodingControl:ListenAddress", "127.0.0.1", "--TranscodingControl:Port", ports.ControlPort.ToString(),
            "--TranscodingControl:UseTls", "false");
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var identity = await CreateAsync(fixture, "Transcoding");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var old = await ConnectAsync(fixture, ports.ControlPort, identity, timeout.Token);
        await using var current = await ConnectAsync(fixture, ports.ControlPort, identity, timeout.Token);
        await old.DisposeAsync();
        await current.ExchangeAsync<WorkerHeartbeat, WorkerState>("Heartbeat", "State", new(), timeout.Token);
        Assert.True(Online(await ReadAsync(fixture, timeout.Token), identity.Id));
        await current.DisposeAsync();
        Assert.False(Online(await WaitOfflineAsync(fixture, identity.Id, timeout.Token), identity.Id));
    }

    private static async Task<(string Id, string Secret, string Kind)> CreateAsync(HttpServerFixture fixture, string kind)
    {
        using var response = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/transcoding/workers", new { name = "same-host-" + kind, kind });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.False(json["worker"]!["isOnline"]?.GetValue<bool>() ?? false);
        return (json["worker"]!["id"]!.GetValue<string>(), json["secret"]!.GetValue<string>(), kind);
    }

    private static async Task<WorkerControlClient> ConnectAsync(HttpServerFixture fixture, int port,
        (string Id, string Secret, string Kind) identity, CancellationToken cancellationToken)
    {
        var connection = await WorkerControlClient.ConnectAsync(new WorkerConnectionOptions
        {
            ApiBaseUrl = fixture.Address.AbsoluteUri, ControlHost = "127.0.0.1", ControlPort = port,
            ControlUseTls = false, WorkerId = identity.Id, Secret = identity.Secret
        }, cancellationToken);
        try
        {
            await connection.ExchangeAsync<WorkerHello, WorkerWelcome>("Hello", "Welcome", new()
            {
                WorkerId = identity.Id, Secret = identity.Secret, Kind = identity.Kind
            }, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<JsonObject[]> ReadAsync(HttpServerFixture fixture, CancellationToken cancellationToken) =>
        (await fixture.Admin.Client.GetFromJsonAsync<JsonArray>("/api/transcoding/workers", cancellationToken))!
            .Select(value => value!.AsObject()).ToArray();

    private static bool Online(IEnumerable<JsonObject> workers, string id) =>
        workers.Single(row => row["id"]!.GetValue<string>() == id)["isOnline"]?.GetValue<bool>() ?? false;

    private static async Task<JsonObject[]> WaitOfflineAsync(HttpServerFixture fixture, string id, CancellationToken cancellationToken)
    {
        while (true)
        {
            var rows = await ReadAsync(fixture, cancellationToken);
            if (!Online(rows, id))
            {
                return rows;
            }
            await Task.Delay(20, cancellationToken);
        }
    }
}
