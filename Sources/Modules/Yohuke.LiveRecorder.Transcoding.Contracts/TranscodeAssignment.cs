namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>不可变源版本和编码配方的执行快照。</summary>
public sealed record TranscodeAssignment
{
    public string JobId { get; set; } = "";
    public int Attempt { get; set; }
    public int CheckpointGeneration { get; set; }
    public string LeaseToken { get; set; } = "";
    public int LeaseSeconds { get; set; } = 90;
    public string AssetId { get; set; } = "";
    public string SourceVersion { get; set; } = "";
    public string RecipeHash { get; set; } = "";
    public long SourceLength { get; set; }
    public double SourceDurationMs { get; set; }
    public string SourceETag { get; set; } = "";
    public string SourceFileName { get; set; } = "source.flv";
    public TranscodeProfile Profile { get; set; } = new();
}
