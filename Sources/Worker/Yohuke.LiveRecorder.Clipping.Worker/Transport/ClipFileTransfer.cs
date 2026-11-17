using System.Net;
using System.Security.Cryptography;
using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.Clipping.Worker.Transport;

internal sealed class ClipFileTransfer : IDisposable
{
    private const int MAX_DOWNLOAD_RETRIES = 4;
    private readonly WorkerTransferClient client;

    internal ClipFileTransfer(WorkerConnectionOptions options, HttpMessageHandler? handler = null, TimeSpan? networkTimeout = null)
    {
        client = new(options, "api/worker/clipping/jobs/", "X-Worker-Attempt", "X-Worker-Lease", handler, networkTimeout);
    }

    internal async Task DownloadAsync(ClipAssignment assignment, ClipInputSpan input, string destination, long bytesPerSecond,
        CancellationToken cancellationToken, Action<long>? progress = null, Action<string>? log = null)
    {
        var range = input.Range ?? throw new InvalidDataException("A clipping source has no bounded range.");
        if (range.StartOffset < 0 || range.EndOffset <= range.StartOffset || range.EndOffset > range.SourceLength
            || string.IsNullOrWhiteSpace(input.SourceETag) || input.RangeSha256 is not { Length: 64 }
            || !input.RangeSha256.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("The clipping source range or source version is invalid.");
        }
        var length = range.EndOffset - range.StartOffset;
        if (File.Exists(destination))
        {
            try
            {
                if (new FileInfo(destination).Length == length)
                {
                    await ValidateContentAsync(destination, input.RangeSha256, cancellationToken);
                    progress?.Invoke(length);
                    return;
                }
            }
            catch (InvalidDataException)
            {
            }
            File.Delete(destination);
        }
        var partial = destination + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var lease = Lease(assignment);
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset < 0 || offset > length)
                {
                    File.Delete(partial);
                    offset = 0;
                }
                if (offset == length)
                {
                    await ValidatePartialAsync(partial, input.RangeSha256, cancellationToken);
                    File.Move(partial, destination, overwrite: false);
                    progress?.Invoke(length);
                    return;
                }
                using var request = client.CreateRequest(HttpMethod.Get, lease, "inputs/" + Uri.EscapeDataString(input.Id));
                request.Headers.Range = new(range.StartOffset + offset, range.EndOffset - 1);
                request.Headers.TryAddWithoutValidation("If-Match", input.SourceETag);
                request.Headers.TryAddWithoutValidation("If-Range", input.SourceETag);
                using var response = await client.SendHeadersAsync(request, cancellationToken);
                await client.RequireSuccessAsync(request, response, cancellationToken);
                var contentRange = response.Content.Headers.ContentRange;
                if (response.StatusCode != HttpStatusCode.PartialContent || contentRange?.Unit != "bytes"
                    || contentRange.From != range.StartOffset + offset || contentRange.To != range.EndOffset - 1
                    || contentRange.Length != range.SourceLength || response.Content.Headers.ContentLength != length - offset
                    || response.Headers.ETag?.ToString() != input.SourceETag)
                {
                    throw new InvalidDataException("The server did not return the exact assigned clipping range and source version.");
                }
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using (var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
                    65536, FileOptions.Asynchronous))
                {
                    output.Position = offset;
                    var buffer = new byte[65536];
                    while (offset < length)
                    {
                        var count = await client.ReadWithTimeoutAsync(source, buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - offset)), cancellationToken);
                        if (count == 0)
                        {
                            throw new EndOfStreamException("The clipping source ended before the assigned range was downloaded.");
                        }
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                        offset += count;
                        progress?.Invoke(offset);
                        await client.WaitForDownloadAsync(count, bytesPerSecond, cancellationToken);
                    }
                    await output.FlushAsync(cancellationToken);
                }
                await ValidatePartialAsync(partial, input.RangeSha256, cancellationToken);
                File.Move(partial, destination, overwrite: false);
                return;
            }
            catch (Exception error) when (attempt < MAX_DOWNLOAD_RETRIES && !cancellationToken.IsCancellationRequested
                && WorkerTransferClient.CanRetry(error))
            {
                log?.Invoke("Retrying the assigned clipping range: " + error.Message);
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken);
            }
        }
    }

    internal async Task UploadAsync(ClipAssignment assignment, string outputPath, ClipOutputManifest manifest,
        long bytesPerSecond, CancellationToken cancellationToken, Action<long>? progress = null,
        Action<string>? log = null, Action? completing = null)
    {
        await UploadOutputAsync(assignment, outputPath, manifest, bytesPerSecond, cancellationToken, progress, log);
        completing?.Invoke();
        await CompleteAsync(assignment, manifest, cancellationToken, log);
    }

    internal Task BeginUploadAsync(ClipAssignment assignment, ClipOutputManifest manifest, CancellationToken cancellationToken) =>
        client.PostJsonAsync(Lease(assignment), "upload-session", new WorkerUploadSession
        {
            ManifestFingerprint = WorkerOutputManifestFingerprint.Create(manifest)
        }, cancellationToken);

    internal Task UploadOutputAsync(ClipAssignment assignment, string outputPath, ClipOutputManifest manifest,
        long bytesPerSecond, CancellationToken cancellationToken, Action<long>? progress = null, Action<string>? log = null) =>
        client.UploadFileAsync(Lease(assignment), outputPath, "output", manifest.Bytes, bytesPerSecond, cancellationToken, progress, log);

    internal async Task CompleteAsync(ClipAssignment assignment, ClipOutputManifest manifest, CancellationToken cancellationToken, Action<string>? log = null)
    {
        var validationBytes = manifest.Bytes * 2d + assignment.Inputs.Where(input => !input.IsGap)
            .Sum(input => (double)(input.Range!.EndOffset - input.Range.StartOffset));
        var validationTimeout = TimeSpan.FromMilliseconds(Math.Clamp(
            (validationBytes / (1024 * 1024) + 60) * 1000, 60000, uint.MaxValue - 1d));
        await client.PostJsonAsync(Lease(assignment), "complete", manifest, cancellationToken,
            validationTimeout, retries: 3, log: log);
    }

    internal Task ReportResultAsync(ClipAssignment assignment, WorkerJobResult result, CancellationToken cancellationToken) =>
        client.PostJsonAsync(Lease(assignment), "result", result, cancellationToken, retries: 1);

    private static WorkerTransferLease Lease(ClipAssignment assignment) => new()
    {
        JobId = assignment.JobId,
        Attempt = assignment.Attempt,
        LeaseToken = assignment.LeaseToken
    };

    private static async Task ValidateContentAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, cancellationToken));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded clipping range does not match its planned source content.");
        }
    }

    private static async Task ValidatePartialAsync(string path, string expected, CancellationToken cancellationToken)
    {
        try
        {
            await ValidateContentAsync(path, expected, cancellationToken);
        }
        catch (InvalidDataException)
        {
            File.Delete(path);
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose() => client.Dispose();
}
