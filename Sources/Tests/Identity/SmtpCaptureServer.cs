using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class SmtpCaptureServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task worker;
    private readonly X509Certificate2? serverCertificate;
    internal TaskCompletionSource<string> Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    internal SmtpCaptureServer(X509Certificate2? serverCertificate = null)
    {
        this.serverCertificate = serverCertificate;
        listener.Start();
        worker = RunAsync();
    }

    private async Task RunAsync()
    {
        using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
        await using Stream stream = serverCertificate is null ? client.GetStream() : new SslStream(client.GetStream());
        if (stream is SslStream tlsStream)
        {
            await tlsStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCertificate
            }, lifetime.Token);
        }
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("220 localhost identity test SMTP");
        while (!lifetime.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(lifetime.Token);
            if (line is null)
            {
                return;
            }
            if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("250-localhost");
                await writer.WriteLineAsync("250 8BITMIME");
            }
            else if (line == "DATA")
            {
                await writer.WriteLineAsync("354 End with a dot");
                var message = new StringBuilder();
                while ((line = await reader.ReadLineAsync(lifetime.Token)) is not null && line != ".")
                {
                    message.AppendLine(line);
                }
                Delivered.TrySetResult(message.ToString());
                await writer.WriteLineAsync("250 queued");
            }
            else if (line == "QUIT")
            {
                await writer.WriteLineAsync("221 goodbye");
                return;
            }
            else
            {
                await writer.WriteLineAsync("250 ok");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        listener.Stop();
        try
        {
            await worker;
        }
        catch (OperationCanceledException)
        {
        }
        catch (AuthenticationException) when (serverCertificate is not null)
        {
        }
        catch (IOException) when (serverCertificate is not null)
        {
        }
        finally
        {
            lifetime.Dispose();
        }
    }
}
