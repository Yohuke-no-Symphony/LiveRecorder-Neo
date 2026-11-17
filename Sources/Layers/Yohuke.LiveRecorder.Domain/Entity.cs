namespace Yohuke.LiveRecorder.Domain;

/// <summary>具有稳定标识和创建时间的管理元数据。</summary>
public abstract class Entity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
