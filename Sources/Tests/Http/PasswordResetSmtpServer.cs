using System.Net;
using System.Net.Sockets;
using System.Text;
using MimeKit;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class PasswordResetSmtpServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource<string> message = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task worker;
    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    internal Task<string> Message => message.Task;
    internal string? Recipient { get; private set; }

    internal PasswordResetSmtpServer()
    {
        listener.Start();
        worker = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(stopping.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 localhost SMTP test");
            while (await reader.ReadLineAsync(stopping.Token) is { } line)
            {
                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("250-localhost");
                    await writer.WriteLineAsync("250 OK");
                }
                else if (line == "DATA")
                {
                    await writer.WriteLineAsync("354 Send message");
                    var body = new StringBuilder();
                    while (await reader.ReadLineAsync(stopping.Token) is { } part && part != ".")
                    {
                        body.Append(part.StartsWith("..", StringComparison.Ordinal) ? part[1..] : part).Append("\r\n");
                    }
                    await using var data = new MemoryStream(Encoding.UTF8.GetBytes(body.ToString()));
                    var parsed = await MimeMessage.LoadAsync(data, stopping.Token);
                    Recipient = parsed.To.Mailboxes.Single().Address;
                    message.TrySetResult(parsed.TextBody ?? throw new InvalidDataException("Expected a plain-text password reset email."));
                    await writer.WriteLineAsync("250 Accepted");
                }
                else if (line == "QUIT")
                {
                    await writer.WriteLineAsync("221 Bye");
                    return;
                }
                else
                {
                    await writer.WriteLineAsync("250 OK");
                }
            }
        }
        catch (Exception error) when (stopping.IsCancellationRequested && error is OperationCanceledException or SocketException or IOException)
        {
        }
        catch (Exception error)
        {
            message.TrySetException(error);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        listener.Stop();
        await worker;
        stopping.Dispose();
    }
}
