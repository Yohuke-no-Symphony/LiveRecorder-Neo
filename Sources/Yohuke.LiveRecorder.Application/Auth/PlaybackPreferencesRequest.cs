using System.Text.Json.Serialization;

namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>当前账号在录播回放中共用的界面偏好。</summary>
public sealed record PlaybackPreferencesRequest([property: JsonRequired] int EventRetentionCount);
