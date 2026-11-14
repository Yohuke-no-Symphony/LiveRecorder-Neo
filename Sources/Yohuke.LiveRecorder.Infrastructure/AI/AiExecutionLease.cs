namespace Yohuke.LiveRecorder.Infrastructure.AI;

internal sealed class AiExecutionLease
{
    public string Id { get; set; } = "global";
    public string Owner { get; set; } = "";
    public DateTime UntilUtc { get; set; }
}
