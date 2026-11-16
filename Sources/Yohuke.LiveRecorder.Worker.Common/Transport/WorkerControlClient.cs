using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Transport;

internal sealed class WorkerControlClient : IAsyncDisposable
{
    private readonly TcpClient client;
    private readonly Stream stream;

    private WorkerControlClient(TcpClient client, Stream stream)
    {
        this.client = client;
        this.stream = stream;
    }

    internal static async Task<WorkerControlClient> ConnectAsync(WorkerConnectionOptions options, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(options.ControlHost, options.ControlPort, timeout.Token);
            Stream stream = client.GetStream();
            try
            {
                if (options.ControlUseTls)
                {
                    var tls = new SslStream(stream, false,
                        (_, certificate, _, errors) => WorkerCertificatePolicy.Validate(options, certificate, errors));
                    stream = tls;
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = options.TlsServerName ?? options.ControlHost,
                        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
                    }, timeout.Token);
                }
                return new(client, stream);
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal async Task<TResponse> ExchangeAsync<TRequest, TResponse>(string type, string expectedType, TRequest payload,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var requestId = Guid.NewGuid().ToString("N");
        await WorkerProtocol.WriteAsync(stream, type, requestId, payload, timeout.Token);
        var message = await WorkerProtocol.ReadAsync(stream, timeout.Token)
            ?? throw new IOException("The server closed the control connection.");
        if (message.Type == "Error" && message.RequestId == requestId)
        {
            var statusCode = 0;
            if (message.Payload.ValueKind == JsonValueKind.Object
                && message.Payload.TryGetProperty("statusCode", out var status) && status.ValueKind == JsonValueKind.Number)
            {
                status.TryGetInt32(out statusCode);
            }
            Exception error = statusCode switch
            {
                401 => new WorkerAuthenticationException("Worker credentials were rejected (status 401). Run register to update the binding."),
                403 => new WorkerAuthenticationException("Worker access was denied (status 403). Check that the worker is enabled and its credentials are current."),
                409 => new InvalidOperationException("Worker configuration conflict (status 409). Run 'config' to review the latest server configuration and retry."),
                0 => new InvalidOperationException("The server rejected the control request without a valid status code."),
                _ => new InvalidOperationException($"The server rejected the control request (status {statusCode}).")
            };
            error.Data["ServerStatusCode"] = statusCode;
            if (message.Payload.ValueKind == JsonValueKind.Object
                && message.Payload.TryGetProperty("message", out var detail) && detail.ValueKind == JsonValueKind.String)
            {
                error.Data["ServerMessage"] = detail.GetString();
            }
            throw error;
        }
        if (message.Type != expectedType || message.RequestId != requestId)
        {
            throw new InvalidDataException("The control response did not match the requested message type or request ID.");
        }
        return message.Payload.Deserialize<TResponse>(WorkerProtocol.JsonOptions)
            ?? throw new InvalidDataException("The server returned an empty control response.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync();
        client.Dispose();
    }
}
