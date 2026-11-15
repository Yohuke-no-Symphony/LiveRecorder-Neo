using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Transport;

internal sealed class WorkerControlClient : IAsyncDisposable
{
    private readonly TcpClient client;
    private readonly SslStream stream;

    private WorkerControlClient(TcpClient client, SslStream stream)
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
            var stream = new SslStream(client.GetStream(), false,
                (_, certificate, _, errors) => WorkerCertificatePolicy.Validate(options, certificate, errors));
            try
            {
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = options.TlsServerName ?? options.ControlHost,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
                }, timeout.Token);
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
            ?? throw new IOException("主站关闭了控制连接");
        if (message.Type == "Error" && message.RequestId == requestId)
        {
            var description = message.Payload.TryGetProperty("message", out var detail) ? detail.GetString() : "主站拒绝了控制请求";
            if (message.Payload.TryGetProperty("statusCode", out var status) && status.GetInt32() is 401 or 403)
            {
                throw new WorkerAuthenticationException(description ?? "Worker 身份已失效");
            }
            throw new InvalidOperationException(description + "；请重新读取主站配置后重试");
        }
        if (message.Type != expectedType || message.RequestId != requestId)
        {
            throw new InvalidDataException("主站控制响应不匹配：" + message.Type);
        }
        return message.Payload.Deserialize<TResponse>(WorkerProtocol.JsonOptions)
            ?? throw new InvalidDataException("主站返回了空控制响应");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync();
        client.Dispose();
    }
}
