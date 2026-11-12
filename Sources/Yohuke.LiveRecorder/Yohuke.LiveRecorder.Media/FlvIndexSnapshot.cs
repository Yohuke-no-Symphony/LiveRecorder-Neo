using System.Text.Json;

namespace Yohuke.LiveRecorder.Media;

internal sealed class FlvIndexSnapshot
{
    internal List<FlvIndexEntry> Entries { get; } = [];
    internal IEnumerable<FlvIndexEntry> Keyframes => Entries.Where(entry => entry.Kind == "Keyframe");
    internal double DurationMs => Entries.Where(entry => entry.Kind == "Committed").Select(entry => entry.TimestampMs).DefaultIfEmpty().Max();

    internal static async Task<FlvIndexSnapshot> ReadAsync(string path, long committedBytes, CancellationToken cancellationToken)
    {
        var snapshot = new FlvIndexSnapshot();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16384, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            FlvIndexEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<FlvIndexEntry>(line, MediaJson.Options);
            }
            catch (JsonException)
            {
                if (await reader.ReadLineAsync(cancellationToken) is not null)
                {
                    throw;
                }
                break;
            }
            if (entry is not null && entry.Offset <= committedBytes
                && (entry.Kind == "Committed" || entry.Offset + 11 + entry.Size + 4 <= committedBytes))
            {
                snapshot.Entries.Add(entry);
            }
        }
        return snapshot;
    }
}
