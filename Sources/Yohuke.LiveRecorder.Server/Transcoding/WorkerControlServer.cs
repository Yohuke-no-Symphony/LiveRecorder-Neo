using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Server.Configuration;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Server.Transcoding;

/// <summary>为独立转码 Worker 提供有界 TCP 或 TLS 控制连接。</summary>
public sealed class WorkerControlServer(TranscodingCoordinator coordinator, IOptions<TranscodingControlOptions> options,
    ILogger<WorkerControlServer> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, TcpClient> workers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> connections = new();
    private long connectionId;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configuration = options.Value;
        if (!configuration.Enabled)
        {
            return;
        }
        using var certificate = configuration.UseTls
            ? X509CertificateLoader.LoadPkcs12FromFile(configuration.CertificatePath!, configuration.CertificatePassword) : null;
        if (certificate is { HasPrivateKey: false })
        {
            throw new InvalidOperationException("Worker TLS certificate requires a private key.");
        }
        using var slots = new SemaphoreSlim(configuration.MaximumConnections);
        var listener = new TcpListener(IPAddress.Parse(configuration.ListenAddress), configuration.Port);
        listener.Start();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                if (!await slots.WaitAsync(0, stoppingToken))
                {
                    client.Dispose();
                    continue;
                }
                var id = Interlocked.Increment(ref connectionId);
                var task = HandleAsync(client, certificate, slots, stoppingToken);
                connections[id] = task;
                _ = task.ContinueWith(completed => connections.TryRemove(id, out _), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
            await Task.WhenAll(connections.Values);
        }
    }

    private async Task HandleAsync(TcpClient client, X509Certificate2? certificate, SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        string? workerId = null;
        try
        {
            using (client)
            {
                await using Stream stream = certificate is null
                    ? client.GetStream() : new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                handshake.CancelAfter(TimeSpan.FromSeconds(15));
                if (stream is SslStream tls)
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ClientCertificateRequired = false
                    }, handshake.Token);
                }
                var message = await WorkerProtocol.ReadAsync(stream, handshake.Token);
                if (message?.Type != "Hello")
                {
                    throw new InvalidDataException("The first worker message must be Hello.");
                }
                var hello = message.Payload.Deserialize<WorkerHello>(WorkerProtocol.JsonOptions)
                    ?? throw new InvalidDataException("Missing worker credentials.");
                if (!await coordinator.AuthenticateAsync(hello.WorkerId, hello.Secret, handshake.Token))
                {
                    await WorkerProtocol.WriteAsync(stream, "Error", message.RequestId, new { statusCode = 401, message = "Worker authentication failed." }, handshake.Token);
                    return;
                }
                WorkerWelcome welcome;
                try
                {
                    welcome = await coordinator.ConnectAsync(hello, handshake.Token);
                }
                catch (InfrastructureException error)
                {
                    await WorkerProtocol.WriteAsync(stream, "Error", message.RequestId,
                        new { statusCode = error.StatusCode, message = error.Message }, handshake.Token);
                    return;
                }
                catch (ArgumentException)
                {
                    await WorkerProtocol.WriteAsync(stream, "Error", message.RequestId,
                        new { statusCode = 400, message = "Invalid worker configuration." }, handshake.Token);
                    return;
                }
                workerId = hello.WorkerId;
                workers.AddOrUpdate(workerId, static (_, replacement) => replacement, static (_, previous, replacement) =>
                {
                    previous.Dispose();
                    return replacement;
                }, client);
                await WorkerProtocol.WriteAsync(stream, "Welcome", message.RequestId, welcome, handshake.Token);
                while (!stoppingToken.IsCancellationRequested)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    message = await WorkerProtocol.ReadAsync(stream, timeout.Token);
                    if (message is null)
                    {
                        break;
                    }
                    if (message.Type != "Heartbeat")
                    {
                        throw new InvalidDataException("Unexpected worker control message.");
                    }
                    if (!await coordinator.AuthenticateAsync(workerId, hello.Secret, timeout.Token))
                    {
                        await WorkerProtocol.WriteAsync(stream, "Error", message.RequestId, new { statusCode = 401, message = "Worker credentials revoked." }, timeout.Token);
                        break;
                    }
                    var heartbeat = message.Payload.Deserialize<WorkerHeartbeat>(WorkerProtocol.JsonOptions)
                        ?? throw new InvalidDataException("Missing worker heartbeat.");
                    if (heartbeat.Jobs is null or { Count: > 65 } || heartbeat.Recordings is null or { Count: > 64 } || heartbeat.Telemetry is null)
                    {
                        throw new InvalidDataException("Worker reports too many active tasks.");
                    }
                    var state = await coordinator.HeartbeatAsync(workerId, hello.Secret, heartbeat, timeout.Token);
                    if (heartbeat.LogBatch is { } batch)
                    {
                        using var logTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                        logTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                        try
                        {
                            state.LogAcknowledgement = await coordinator.AppendWorkerLogsAsync(workerId, batch, logTimeout.Token);
                        }
                        catch (Exception exception) when (!stoppingToken.IsCancellationRequested && !timeout.IsCancellationRequested)
                        {
                            logger.LogWarning("Worker log batch was not persisted ({WorkerId}): {ErrorType}", workerId, exception.GetType().Name);
                        }
                    }
                    await WorkerProtocol.WriteAsync(stream, "State", message.RequestId, state, timeout.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            logger.LogWarning("Worker control connection ended ({WorkerId}): {ErrorType}", workerId, error.GetType().Name);
        }
        finally
        {
            if (workerId is not null)
            {
                ((ICollection<KeyValuePair<string, TcpClient>>)workers).Remove(new(workerId, client));
            }
            client.Dispose();
            slots.Release();
        }
    }
}
