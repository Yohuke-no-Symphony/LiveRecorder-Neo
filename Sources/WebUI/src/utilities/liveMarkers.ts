import { formatTimecode } from './format'

export function canSeekLiveMarker(offsetMs: number, durationMs: number): boolean
{
  return Number.isFinite(offsetMs) && Number.isFinite(durationMs) && offsetMs >= 0 && offsetMs < durationMs
}

export function formatLiveMarkerOffset(offsetMs: number): string
{
  return `${offsetMs < 0 ? '−' : ''}${formatTimecode(Math.abs(offsetMs))}`
}

export function formatCompactLiveMarkerTime(time: string): string
{
  return time.replace(/(:\d{2})\.\d+$/, '$1').replace(/^([−-]?)00:/, '$1')
}

export function parseLiveMarkerCalibration(seconds: number | string): number | undefined
{
  const text = String(seconds).trim()
  const milliseconds = Number(text) * 1000
  const rounded = Math.round(milliseconds)
  return text && Number.isSafeInteger(rounded) && Math.abs(milliseconds - rounded) <= 0.000001 ? rounded : undefined
}
