using System.Text.Json.Serialization;

namespace Yohuke.LiveRecorder.Application.Library.LiveMarkers;

/// <summary>以毫秒为单位、正值延后的文件整体校准。</summary>
public sealed record LiveMarkerAlignmentRequest([property: JsonRequired] long CalibrationMs);
