namespace Yohuke.LiveRecorder.Domain;

/// <summary>库操作触发的持久资产引用复核意图。</summary>
public sealed class TranscodeAssetReconciliation : Entity
{
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
