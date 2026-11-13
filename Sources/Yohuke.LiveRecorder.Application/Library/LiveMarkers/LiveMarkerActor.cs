namespace Yohuke.LiveRecorder.Application.Library.LiveMarkers;

/// <summary>当前请求已验证的记录所有者身份及跨用户管理能力。</summary>
public sealed record LiveMarkerActor(string Id, string Name, bool CanManageOthers);
