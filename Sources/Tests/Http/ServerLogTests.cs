using Microsoft.Extensions.Logging;
using Xunit;
using Yohuke.LiveRecorder.Server.Logging;

namespace Yohuke.LiveRecorder.Http.Tests;

public sealed class ServerLogTests
{
    [Fact]
    public void CaptureKeepsSafeFieldsAndFrameworkWarnings()
    {
        var buffer = new ServerLogBuffer();
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new ServerLogProvider(buffer, "LogTestHost")));
        var application = factory.CreateLogger("LogTestHost");
        const string secret = "sensitive-value";
        using (application.BeginScope(new Dictionary<string, object?> { ["Cookie"] = secret }))
        {
            application.LogWarning("First-time installation token: {InstallationToken}", secret);
            application.LogWarning(new InvalidOperationException(secret),
                "Room {RoomId} query failed: {Message}. Cookie {Cookie}. Path {Path}.", 431, secret, secret, secret);
            application.Log(LogLevel.Error, new EventId(), secret, null, (value, _) => value);
        }

        var kestrel = factory.CreateLogger("Microsoft.AspNetCore.Server.Kestrel");
        kestrel.LogInformation("Routine request");
        var connectionFailed = LoggerMessage.Define<string, string>(LogLevel.Warning, new(13, "ApplicationError"),
            "Connection '{ConnectionId}' failed with {Header}");
        connectionFailed(kestrel, "0HND:01", secret, null);
        factory.CreateLogger("Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware").LogWarning("Ignored HTTP logging record");
        factory.CreateLogger("System.Net.Http.HttpClient.External").LogError("Ignored external request record");
        factory.CreateLogger("Microsoft.Hosting.Lifetime").LogInformation("Application started");

        Assert.Collection(buffer.Read(null)!.Entries,
            entry =>
            {
                Assert.Equal("Warning", entry.Level);
                Assert.Equal("Room 431 query failed: …. Cookie …. Path ….", entry.Message);
                Assert.Equal(nameof(InvalidOperationException), entry.ExceptionType);
            },
            entry => Assert.Equal("Connection '0HND:01' failed with …", entry.Message),
            entry => Assert.Equal("Application started", entry.Message));
    }

    [Fact]
    public void CaptureBoundsUnicodeMessagesAndRejectsFreeTextInIdentifierFields()
    {
        var buffer = new ServerLogBuffer();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(new ServerLogProvider(buffer, "LogTestHost")));
        var logger = factory.CreateLogger("Yohuke.LiveRecorder.Server");
        logger.LogInformation("{{ log }} {JobId}", "https://example.test/?token=sensitive-value");
        var longMessage = new string('a', 2046) + "😀 truncated";
        KeyValuePair<string, object?>[] fields = [new("{OriginalFormat}", longMessage)];
        logger.Log(LogLevel.Information, new EventId(), fields, null, (_, _) => longMessage);

        Assert.Collection(buffer.Read(null)!.Entries,
            entry => Assert.Equal("{ log } …", entry.Message),
            entry => Assert.Equal(new string('a', 2046) + "…", entry.Message));
    }

    [Fact]
    public void BufferPaginatesConcurrentWritesAndReportsTheOverwriteBoundary()
    {
        var buffer = new ServerLogBuffer();
        var empty = buffer.Read(null)!;
        Assert.Empty(empty.Entries);
        Assert.False(empty.HasMore);
        buffer.Append(LogLevel.Information, "Test", "record-0", null);
        var first = buffer.Read(empty.NextCursor)!;
        Parallel.For(1, 1000, index => buffer.Append(LogLevel.Information, "Test", $"record-{index}", null));

        var page = buffer.Read(empty.NextCursor)!;
        var collected = new List<ServerLogEntry>();
        while (true)
        {
            Assert.Equal(200, page.Entries.Length);
            Assert.Null(page.ResetReason);
            collected.AddRange(page.Entries);
            if (!page.HasMore)
            {
                break;
            }

            page = buffer.Read(page.NextCursor)!;
        }

        Assert.Equal(Enumerable.Range(1, 1000).Select(id => (long)id), collected.Select(entry => entry.Id));
        Assert.Equal(1000, collected.Select(entry => entry.Message).Distinct().Count());
        Assert.Equal(collected.TakeLast(200).ToArray(), buffer.Read(null)!.Entries);
        Assert.Empty(buffer.Read(page.NextCursor)!.Entries);

        buffer.Append(LogLevel.Warning, "Test", "new record", null);
        var stillAvailable = buffer.Read(first.NextCursor)!;
        Assert.Null(stillAvailable.ResetReason);
        Assert.Equal(2, stillAvailable.Entries[0].Id);
        var reset = buffer.Read(empty.NextCursor)!;
        Assert.Equal("bufferOverwritten", reset.ResetReason);
        Assert.Equal(802, reset.Entries[0].Id);
        Assert.Equal(1001, reset.Entries[^1].Id);
        Assert.False(reset.HasMore);
    }

    [Fact]
    public void BufferRejectsInvalidCursorsAndResumesFromTheNewProcess()
    {
        var previous = new ServerLogBuffer();
        previous.Append(LogLevel.Information, "Test", "previous", null);
        var previousCursor = previous.Read(null)!.NextCursor;
        var current = new ServerLogBuffer();
        var reset = current.Read(previousCursor)!;
        Assert.Equal("processRestarted", reset.ResetReason);
        Assert.Empty(reset.Entries);
        Assert.Null(current.Read(""));
        Assert.Null(current.Read("bad-cursor"));
        Assert.Null(current.Read(reset.NextCursor[..^1] + "-1"));
        Assert.Null(current.Read(reset.NextCursor[..^1] + "2"));

        current.Append(LogLevel.Information, "Test", "current", null);
        var page = current.Read(reset.NextCursor)!;
        Assert.Null(page.ResetReason);
        Assert.Equal("current", Assert.Single(page.Entries).Message);
    }
}
