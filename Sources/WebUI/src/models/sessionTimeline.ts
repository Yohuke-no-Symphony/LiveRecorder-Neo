export type TimelineMarkerKind = 'Gift' | 'SuperChat' | 'RedPocket' | 'Guard'

export interface DanmuHeatBucket
{
  offsetMs: number
  count: number
}

export interface TimelineMarkerGroup
{
  offsetMs: number
  endOffsetMs: number
  kind: TimelineMarkerKind
  count: number
  sequence: number
  userName: string
  text: string
  amount: number | null
  quantity: number
}

export interface SessionTimeline
{
  durationMs: number
  bucketWidthMs: number
  revision: string
  heat: DanmuHeatBucket[]
  markers: TimelineMarkerGroup[]
}
