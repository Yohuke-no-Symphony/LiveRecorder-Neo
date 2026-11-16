using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

internal sealed class RecordingConfigurationTestCoordinator : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task server;
    private readonly bool dropUpdateAcknowledgement;
    private WorkerWelcome welcome;

    internal RecordingConfigurationTestCoordinator(WorkerWelcome welcome, bool dropUpdateAcknowledgement = false)
    {
        this.welcome = welcome;
        this.dropUpdateAcknowledgement = dropUpdateAcknowledgement;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        server = RunAsync();
    }

    internal int Port { get; }
    internal ConcurrentQueue<WorkerHello> Hellos { get; } = new();
    internal TaskCompletionSource Heartbeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task RunAsync()
    {
        while (!cancellation.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
            await using var stream = client.GetStream();
            var request = await WorkerProtocol.ReadAsync(stream, cancellation.Token)
                ?? throw new InvalidDataException("Missing test coordinator Hello.");
            var hello = request.Payload.Deserialize<WorkerHello>(WorkerProtocol.JsonOptions)!;
            Hellos.Enqueue(hello);
            if (hello.InitialConfiguration is { } configuration)
            {
                welcome = welcome with { Configuration = configuration, ConfigurationRevision = welcome.ConfigurationRevision + 1 };
                if (dropUpdateAcknowledgement)
                {
                    continue;
                }
            }
            await WorkerProtocol.WriteAsync(stream, "Welcome", request.RequestId, welcome, cancellation.Token);
            while (await WorkerProtocol.ReadAsync(stream, cancellation.Token) is { } heartbeat)
            {
                await WorkerProtocol.WriteAsync(stream, "State", heartbeat.RequestId, new WorkerState
                {
                    Configuration = welcome.Configuration, ConfigurationRevision = welcome.ConfigurationRevision
                }, cancellation.Token);
                Heartbeat.TrySetResult();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync();
        listener.Stop();
        try
        {
            await server;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}
