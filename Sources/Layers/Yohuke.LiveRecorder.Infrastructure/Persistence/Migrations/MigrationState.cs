namespace Yohuke.LiveRecorder.Infrastructure.Persistence.Migrations;

internal sealed class MigrationState
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string? PendingTarget { get; set; }
    public string? Checkpoint { get; set; }
    public string? LeaseId { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
}
