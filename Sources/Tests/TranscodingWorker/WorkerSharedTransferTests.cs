using System.Net;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;
using Xunit;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

/// <summary>Verifies that shared transfer recovery uses the selected task kind and execution identity.</summary>
public sealed class WorkerSharedTransferTests
{
    /// <summary>A clipping upload resumes from committed server bytes without using transcoding routes or headers.</summary>
    [Fact]
    public async Task ClippingUploadRecoversLostResponseUsingItsOwnLeaseHeaders()
    {
        var directory = Path.Combine(Path.GetTempPath(), "worker-shared-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "output.mp4");
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            await File.WriteAllBytesAsync(path, bytes);
            var options = new WorkerConnectionOptions
            {
                ApiBaseUrl = "https://worker.test/",
                WorkerId = "clip-node",
                Secret = "clip-secret"
            };
            var lease = new WorkerTransferLease { JobId = "clip-job", Attempt = 3, LeaseToken = "clip-lease" };
            using var received = new MemoryStream();
            var uploads = 0;
            var completed = false;
            using var transfers = new WorkerTransferClient(options, "api/worker/clipping/jobs", "X-Clip-Attempt", "X-Clip-Lease",
                new WorkerTestHttpHandler(async (request, cancellationToken) =>
                {
                    Assert.Equal("clip-secret", request.Headers.Authorization?.Parameter);
                    Assert.Equal("clip-node", request.Headers.GetValues("X-Worker-Id").Single());
                    Assert.Equal("3", request.Headers.GetValues("X-Clip-Attempt").Single());
                    Assert.Equal("clip-lease", request.Headers.GetValues("X-Clip-Lease").Single());
                    Assert.False(request.Headers.Contains("X-Transcode-Attempt"));
                    Assert.False(request.Headers.Contains("X-Transcode-Lease"));
                    Assert.StartsWith("/api/worker/clipping/jobs/clip-job/", request.RequestUri!.AbsolutePath);
                    if (request.Method == HttpMethod.Put)
                    {
                        uploads++;
                        await request.Content!.CopyToAsync(received, cancellationToken);
                        throw new HttpRequestException("The response was lost after committing the upload.");
                    }
                    if (request.Method == HttpMethod.Post)
                    {
                        completed = true;
                        return new(HttpStatusCode.NoContent);
                    }
                    var response = new HttpResponseMessage(HttpStatusCode.NoContent);
                    response.Headers.Add("Upload-Offset", received.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return response;
                }));

            await transfers.UploadFileAsync(lease, path, "artifacts/output.mp4", bytes.Length, 0, CancellationToken.None);
            await transfers.PostJsonAsync(lease, "complete", new { Length = bytes.Length }, CancellationToken.None);

            Assert.Equal(1, uploads);
            Assert.Equal(bytes, received.ToArray());
            Assert.True(completed);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
