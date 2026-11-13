import type { DanmuHeatBucket, TimelineMarkerGroup, TimelineMarkerKind } from '@/models/sessionTimeline'

export const TIMELINE_MARKER_KINDS: readonly TimelineMarkerKind[] = ['Gift', 'SuperChat', 'RedPocket', 'Guard']
export type DisplayTimelineMarker = TimelineMarkerGroup & { key: string }
export type TimelineHeatColumn = DanmuHeatBucket & { left: number; width: number; strength: number }

export function groupTimelineMarkers(markers: readonly TimelineMarkerGroup[], durationMs: number, columns: number): DisplayTimelineMarker[]
{
  if (!Number.isFinite(durationMs) || durationMs <= 0)
  {
    return []
  }
  const cells = Number.isFinite(columns) ? Math.max(1, Math.floor(columns)) : 1
  const groups = new Map<string, DisplayTimelineMarker>()
  for (const marker of markers)
  {
    if (!TIMELINE_MARKER_KINDS.includes(marker.kind) || !Number.isFinite(marker.offsetMs) || marker.offsetMs < 0 || marker.offsetMs > durationMs || !Number.isFinite(marker.count) || marker.count <= 0)
    {
      continue
    }
    const cell = Math.min(cells - 1, Math.floor(marker.offsetMs / durationMs * cells))
    const key = `${marker.kind}:${cell}`
    const endOffsetMs = Math.max(marker.offsetMs, Math.min(durationMs, Number.isFinite(marker.endOffsetMs) ? marker.endOffsetMs : marker.offsetMs))
    const previous = groups.get(key)
    if (!previous)
    {
      groups.set(key, { ...marker, endOffsetMs, key })
      continue
    }
    const first = marker.offsetMs < previous.offsetMs || (marker.offsetMs === previous.offsetMs && marker.sequence < previous.sequence) ? marker : previous
    groups.set(key, {
      ...first, key, offsetMs: Math.min(previous.offsetMs, marker.offsetMs), endOffsetMs: Math.max(previous.endOffsetMs, endOffsetMs),
      count: previous.count + marker.count, quantity: previous.quantity + marker.quantity,
      amount: previous.amount === null && marker.amount === null ? null : (previous.amount ?? 0) + (marker.amount ?? 0),
    })
  }
  return [...groups.values()].sort((a, b) => a.offsetMs - b.offsetMs || TIMELINE_MARKER_KINDS.indexOf(a.kind) - TIMELINE_MARKER_KINDS.indexOf(b.kind))
}

export function timelineHeatColumns(buckets: readonly DanmuHeatBucket[], bucketWidthMs: number, durationMs: number): TimelineHeatColumn[]
{
  if (!Number.isFinite(durationMs) || durationMs <= 0 || !Number.isFinite(bucketWidthMs) || bucketWidthMs <= 0)
  {
    return []
  }
  const valid = buckets.filter(item => Number.isFinite(item.offsetMs) && item.offsetMs >= 0 && item.offsetMs < durationMs && Number.isFinite(item.count) && item.count > 0)
  const maximum = valid.reduce((value, item) => Math.max(value, item.count), 1)
  return valid.map(item => ({ ...item, left: item.offsetMs / durationMs * 100, width: Math.min(bucketWidthMs, durationMs - item.offsetMs) / durationMs * 100, strength: Math.sqrt(item.count / maximum) }))
}
