using System.Net;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

/// <summary>Verifies that recording uploads transfer only coherent committed prefixes of growing files.</summary>
public sealed class RecordingAppendTests
{
    /// <summary>Growing capture files resume at the remote offset and never transfer the uncommitted tail.</summary>
    [Fact]
    public async Task AppendsOnlyCommittedBytesAndResumesAsTheFileGrows()
    {
        using var fixture = new RecordingWorkerFixture();
        var bytes = Enumerable.Range(0, 200).Select(value => (byte)value).ToArray();
        var path = Path.Combine(fixture.DirectoryPath, "capture.flv");
        await File.WriteAllBytesAsync(path, bytes);
        using var received = new MemoryStream();
        var offsets = new List<long>();
        using var handler = new RecordingTestHttpHandler(async (request, token) =>
        {
            Assert.Equal("2", request.Headers.GetValues("X-Worker-Generation").Single());
            Assert.Equal("token", request.Headers.GetValues("X-Worker-Token").Single());
            if (request.Method == HttpMethod.Put)
            {
                offsets.Add(long.Parse(request.Headers.GetValues("Upload-Offset").Single(), System.Globalization.CultureInfo.InvariantCulture));
                await request.Content!.CopyToAsync(received, token);
            }
            var response = new HttpResponseMessage(HttpStatusCode.NoContent);
            response.Headers.Add("Upload-Offset", received.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return response;
        });
        using var transfer = new WorkerTransferClient(new() { ApiBaseUrl = "https://test.invalid", WorkerId = "worker", Secret = "secret" },
            "api/worker/recording/targets", "X-Worker-Generation", "X-Worker-Token", handler);
        var lease = new WorkerTransferLease { JobId = "assignment", Attempt = 2, LeaseToken = "token" };

        await transfer.AppendFileAsync(lease, path, "media", 100, 0, CancellationToken.None);
        Assert.Equal(bytes[..100], received.ToArray());
        await transfer.AppendFileAsync(lease, path, "media", 150, 0, CancellationToken.None);

        Assert.Equal(bytes[..150], received.ToArray());
        Assert.Equal(new long[] { 0, 100 }, offsets);
        Assert.Equal(200, new FileInfo(path).Length);
    }
}
