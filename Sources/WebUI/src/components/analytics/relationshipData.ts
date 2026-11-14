import type { AnalyticsBucket, OnlineInterval } from '@/models/media'
import type { AnalyticsRange } from '@/models/analyticsRange'

export type RelationshipMetric = 'danmuCount' | 'superChatCount' | 'superChatAmount'

export function clampAnalyticsRange(fromMs: number, toMs: number, durationMs: number): AnalyticsRange | undefined
{
  if (![fromMs, toMs, durationMs].every(Number.isFinite) || durationMs <= 0)
  {
    return undefined
  }
  const from = Math.max(0, Math.min(fromMs, durationMs))
  const to = Math.max(0, Math.min(toMs, durationMs))
  return to > from ? { fromMs: from, toMs: to } : undefined
}

export function onlineLineData(intervals: OnlineInterval[]): (number | null)[][]
{
  const valid = intervals.filter(item => [item.fromMs, item.toMs, item.count].every(Number.isFinite) && item.fromMs >= 0 && item.toMs > item.fromMs && item.count >= 0)
    .sort((first, second) => first.fromMs - second.fromMs)
  const points: (number | null)[][] = []
  for (let index = 0; index < valid.length; index++)
  {
    const item = valid[index]!
    points.push([item.fromMs, item.count], [item.toMs, item.count])
    if (!valid[index + 1] || valid[index + 1]!.fromMs > item.toMs)
    {
      points.push([item.toMs, null])
    }
  }
  return points
}

export function relationshipPoints(buckets: AnalyticsBucket[], metric: RelationshipMetric, range: AnalyticsRange): number[][]
{
  return buckets.filter(bucket => bucket.offsetMs >= range.fromMs && bucket.offsetMs + 60000 <= range.toMs
      && bucket.durationMs === 60000 && bucket.onlineAverage !== null && Number.isFinite(bucket.onlineAverage)
      && bucket.onlineObservedMs >= 48000 && Number.isFinite(bucket[metric]))
    .map(bucket => [bucket.onlineAverage!, bucket[metric], bucket.offsetMs])
}
