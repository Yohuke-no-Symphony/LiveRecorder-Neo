using System.Net;
using System.Net.Http.Headers;
using Xunit;
using Yohuke.LiveRecorder.Clipping.Worker.Transport;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.Clipping.Worker.Tests;

/// <summary>Verifies bounded clipping downloads and source-version fencing.</summary>
public sealed class ClipTransferTests
{
    /// <summary>Every request names the exact input range and clipping lease headers.</summary>
    [Fact]
    public async Task DownloadsOnlyAssignedBytesWithIndependentLeaseHeaders()
    {
        using var fixture = new ClipTestFixture();
        var assignment = ClipTestFixture.SmallAssignment();
        var input = assignment.Inputs[0];
        var requests = 0;
        using var handler = new ClipTestHttpHandler((request, _) =>
        {
            requests++;
            Assert.Equal("/api/worker/clipping/jobs/job/inputs/source", request.RequestUri!.AbsolutePath);
            Assert.Equal("bytes=100-199", request.Headers.Range!.ToString());
            Assert.Equal("Bearer clip-secret", request.Headers.Authorization!.ToString());
            Assert.Equal("clip-worker", request.Headers.GetValues("X-Worker-Id").Single());
            Assert.Equal("1", request.Headers.GetValues("X-Worker-Attempt").Single());
            Assert.Equal("lease", request.Headers.GetValues("X-Worker-Lease").Single());
            Assert.Equal(input.SourceETag, request.Headers.IfMatch.Single().ToString());
            return Task.FromResult(Response());
        });
        using var transfer = new ClipFileTransfer(fixture.Connection, handler);
        var destination = Path.Combine(fixture.Root, "input.range");

        await transfer.DownloadAsync(assignment, input, destination, 0, CancellationToken.None);

        Assert.Equal(100, new FileInfo(destination).Length);
        Assert.Equal(1, requests);
        Assert.False(File.Exists(destination + ".partial"));
    }

    /// <summary>Ignoring Range or changing its boundaries, length or version never falls back to the full source.</summary>
    [Theory]
    [InlineData("status")]
    [InlineData("start")]
    [InlineData("end")]
    [InlineData("source-length")]
    [InlineData("body-length")]
    [InlineData("etag")]
    public async Task RejectsUnexpectedRangeResponse(string difference)
    {
        using var fixture = new ClipTestFixture();
        var assignment = ClipTestFixture.SmallAssignment();
        var requests = 0;
        using var handler = new ClipTestHttpHandler((_, _) =>
        {
            requests++;
            var response = Response();
            switch (difference)
            {
                case "status": response.StatusCode = HttpStatusCode.OK; break;
                case "start": response.Content.Headers.ContentRange = new(99, 199, 1000); break;
                case "end": response.Content.Headers.ContentRange = new(100, 200, 1000); break;
                case "source-length": response.Content.Headers.ContentRange = new(100, 199, 1001); break;
                case "body-length": response.Content.Headers.ContentLength = 101; break;
                case "etag": response.Headers.ETag = new("\"changed\""); break;
                default: throw new ArgumentOutOfRangeException(nameof(difference));
            }
            return Task.FromResult(response);
        });
        using var transfer = new ClipFileTransfer(fixture.Connection, handler);
        var destination = Path.Combine(fixture.Root, "input.range");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            transfer.DownloadAsync(assignment, assignment.Inputs[0], destination, 0, CancellationToken.None));

        Assert.Equal(1, requests);
        Assert.False(File.Exists(destination));
    }

    /// <summary>A partial local range resumes at its absolute source offset with the same fixed end.</summary>
    [Fact]
    public async Task ResumesWithinAssignedRange()
    {
        using var fixture = new ClipTestFixture();
        var assignment = ClipTestFixture.SmallAssignment();
        var destination = Path.Combine(fixture.Root, "input.range");
        await File.WriteAllBytesAsync(destination + ".partial", new byte[40]);
        using var handler = new ClipTestHttpHandler((request, _) =>
        {
            Assert.Equal("bytes=140-199", request.Headers.Range!.ToString());
            var response = Response();
            response.Content = new ByteArrayContent(new byte[60]);
            response.Content.Headers.ContentRange = new(140, 199, 1000);
            return Task.FromResult(response);
        });
        using var transfer = new ClipFileTransfer(fixture.Connection, handler);

        await transfer.DownloadAsync(assignment, assignment.Inputs[0], destination, 0, CancellationToken.None);

        Assert.Equal(100, new FileInfo(destination).Length);
    }

    /// <summary>A revoked clipping lease stops the transfer immediately.</summary>
    [Fact]
    public async Task LeaseConflictStopsWithoutRetry()
    {
        using var fixture = new ClipTestFixture();
        var assignment = ClipTestFixture.SmallAssignment();
        var requests = 0;
        using var handler = new ClipTestHttpHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
        });
        using var transfer = new ClipFileTransfer(fixture.Connection, handler);

        await Assert.ThrowsAsync<WorkerLeaseLostException>(() => transfer.DownloadAsync(assignment, assignment.Inputs[0],
            Path.Combine(fixture.Root, "input.range"), 0, CancellationToken.None));

        Assert.Equal(1, requests);
    }

    /// <summary>A permanent range rejection never retries the same invalid request.</summary>
    [Fact]
    public async Task PermanentRangeRejectionDoesNotRetry()
    {
        using var fixture = new ClipTestFixture();
        var assignment = ClipTestFixture.SmallAssignment();
        var requests = 0;
        using var handler = new ClipTestHttpHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
        });
        using var transfer = new ClipFileTransfer(fixture.Connection, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => transfer.DownloadAsync(assignment, assignment.Inputs[0],
            Path.Combine(fixture.Root, "input.range"), 0, CancellationToken.None));

        Assert.Equal(1, requests);
    }

    /// <summary>Overwriting a source without changing its size or response ETag cannot bypass the planned content identity.</summary>
    [Fact]
    public async Task RejectsChangedContentWithMatchingRangeAndEtag()
    {
        using var fixture = new ClipTestFixture();
        var assignment = ClipTestFixture.SmallAssignment();
        using var handler = new ClipTestHttpHandler((_, _) =>
        {
            var response = Response();
            var content = new byte[100];
            content[50] = 1;
            response.Content = new ByteArrayContent(content);
            response.Content.Headers.ContentRange = new(100, 199, 1000);
            return Task.FromResult(response);
        });
        using var transfer = new ClipFileTransfer(fixture.Connection, handler);
        var destination = Path.Combine(fixture.Root, "changed.range");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            transfer.DownloadAsync(assignment, assignment.Inputs[0], destination, 0, CancellationToken.None));

        Assert.False(File.Exists(destination));
    }

    private static HttpResponseMessage Response()
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[100]) };
        response.Headers.ETag = new EntityTagHeaderValue("\"source\"");
        response.Content.Headers.ContentRange = new(100, 199, 1000);
        return response;
    }
}
