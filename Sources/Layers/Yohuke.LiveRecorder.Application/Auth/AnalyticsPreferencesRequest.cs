using System.Text.Json.Serialization;

namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>当前账号的直播分析界面偏好。</summary>
public sealed record AnalyticsPreferencesRequest([property: JsonRequired] bool RangePinned);
