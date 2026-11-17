namespace Yohuke.LiveRecorder.Worker.Common.Storage;

internal sealed record WorkerArtifact
{
    public string RelativePath { get; set; } = "";
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
}
