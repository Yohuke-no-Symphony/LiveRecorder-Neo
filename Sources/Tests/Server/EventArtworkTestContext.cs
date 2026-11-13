using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Tests;

internal sealed class EventArtworkTestContext : IAsyncDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "recorder-event-artwork-" + Guid.NewGuid().ToString("N"));
    internal AppPaths Paths { get; }
    internal AudienceArtworkHttpHandler Http { get; } = new();
    internal AudienceAvatarTestSource Source { get; } = new();
    internal AudienceAvatarService Avatars { get; }
    internal EventArtworkService Artwork { get; }
    internal BroadcastSession Session { get; } = new() { DirectoryPath = "session" };

    internal EventArtworkTestContext()
    {
        Paths = new(directory, Path.Combine(directory, "records"));
        Avatars = new(Source, TimeProvider.System, NullLogger<AudienceAvatarService>.Instance);
        Artwork = new(new SessionEventReader(new EventArchiveReader()), Paths, new ArtworkCache(Http, Paths), Avatars);
    }

    internal async Task WriteAsync(RecordedEventEnvelope item)
    {
        var source = Session.EventSources.FirstOrDefault()?.DirectoryPath ?? Session.DirectoryPath;
        var target = Path.Combine(Paths.RecordsDirectory, source, "弹幕");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "事件.jsonl"), JsonSerializer.Serialize(item, JsonFiles.Options) + "\n");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Avatars.DisposeAsync();
        Http.Dispose();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
