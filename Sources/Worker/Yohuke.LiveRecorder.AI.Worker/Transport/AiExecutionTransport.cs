using Yohuke.LiveRecorder.AI.Contracts;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.AI.Worker.Transport;

internal sealed class AiExecutionTransport : IAiExecutionTransport, IDisposable
{
    private readonly WorkerTransferClient client;
    private readonly Func<long> uploadLimit;
    private readonly TransferThrottle uploadThrottle = new();

    internal AiExecutionTransport(WorkerConnectionOptions options, HttpMessageHandler? handler = null, Func<long>? uploadLimit = null)
    {
        client = new(options, "api/worker/ai/jobs", "X-Ai-Attempt", "X-Ai-Lease-Token", handler);
        this.uploadLimit = uploadLimit ?? (() => 0);
    }

    public Task<AiExecutionSpecification> GetExecutionAsync(AiAssignment assignment, CancellationToken cancellationToken) =>
        client.GetJsonAsync<AiExecutionSpecification>(Lease(assignment), "execution", cancellationToken);

    public Task<AiCheckpointAcknowledgement> CheckpointAsync(AiAssignment assignment, AiExecutionCheckpoint payload, CancellationToken cancellationToken) =>
        PostAsync(assignment, "checkpoint", payload, cancellationToken);

    public Task<AiCheckpointAcknowledgement> CompleteAsync(AiAssignment assignment, AiExecutionResult payload, CancellationToken cancellationToken) =>
        PostAsync(assignment, "complete", payload, cancellationToken);

    private async Task<AiCheckpointAcknowledgement> PostAsync<T>(AiAssignment assignment, string endpoint, T payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, WorkerProtocol.JsonOptions);
        var limit = uploadLimit();
        using var request = client.CreateRequest(HttpMethod.Post, Lease(assignment), endpoint);
        request.Content = new AiJsonUploadContent(bytes, uploadThrottle, limit);
        var milliseconds = 30000d + (limit > 0 ? (double)bytes.Length / limit * 1000 : 0);
        using var response = await client.SendHeadersAsync(request, cancellationToken, TimeSpan.FromMilliseconds(Math.Min(uint.MaxValue - 1, milliseconds)));
        await client.RequireSuccessAsync(request, response, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await response.Content.ReadFromJsonAsync<AiCheckpointAcknowledgement>(WorkerProtocol.JsonOptions, timeout.Token)
            ?? throw new InvalidDataException("The coordinator returned an empty AI acknowledgement.");
    }

    public async Task DownloadAsync(AiAssignment assignment, AiInputSource source, string destination, long bytesPerSecond, CancellationToken cancellationToken)
    {
        if (source.Length < 0)
        {
            throw new InvalidDataException("The AI input length is invalid.");
        }
        if (File.Exists(destination) && new FileInfo(destination).Length == source.Length)
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var request = client.CreateRequest(HttpMethod.Get, Lease(assignment), "sources/" + Uri.EscapeDataString(source.Id));
            using var response = await client.SendHeadersAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Gone)
            {
                throw new InvalidDataException("The fixed AI input is no longer available.");
            }
            await client.RequireSuccessAsync(request, response, cancellationToken);
            if (response.Content.Headers.ContentLength is { } declared && declared != source.Length)
            {
                throw new InvalidDataException("The server returned a different AI input length.");
            }
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            await using (var output = new FileStream(temporary, options))
            {
                var buffer = new byte[65536];
                var received = 0L;
                while (await client.ReadWithTimeoutAsync(input, buffer, cancellationToken) is var count && count != 0)
                {
                    if (count > source.Length - received)
                    {
                        throw new InvalidDataException("The AI input exceeds its fixed length.");
                    }
                    await client.WaitForDownloadAsync(count, bytesPerSecond, cancellationToken);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    received += count;
                }
                if (received != source.Length)
                {
                    throw new InvalidDataException("The AI input is incomplete.");
                }
                output.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static WorkerTransferLease Lease(AiAssignment assignment) => new()
    {
        JobId = assignment.JobId, Attempt = assignment.Attempt, LeaseToken = assignment.LeaseToken
    };

    public void Dispose() => client.Dispose();
}
