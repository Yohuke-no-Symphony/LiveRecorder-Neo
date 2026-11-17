namespace Yohuke.LiveRecorder.Infrastructure.Tasks;

internal sealed record TaskHistoryCursor(DateTime CreatedAtUtc, string Kind, string Id);
