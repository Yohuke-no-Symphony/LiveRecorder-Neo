using Yohuke.LiveRecorder.Application.Library;

namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>同主播已结束场次的显式归组。</summary>
public sealed record SessionGroupRequest(List<string> SessionIds, string? Title = null, SessionGroupConfiguration? Configuration = null);
