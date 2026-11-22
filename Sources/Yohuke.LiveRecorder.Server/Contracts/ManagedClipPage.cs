namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>一页全站有效切片及表格分页所需的总数。</summary>
public sealed record ManagedClipPage(IReadOnlyList<ManagedClipResponse> Items, long Total);
