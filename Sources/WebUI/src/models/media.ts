export interface Entity
{
  id: string
  createdAtUtc: string
  updatedAtUtc?: string
}

export interface Streamer extends Entity
{
  uid: number
  roomId: number
  name: string
  avatarUrl: string
  enabled: boolean
  isMonitored: boolean
  accountId?: string | null
  quality?: number | null
  recordDanmu?: boolean | null
  segmentBytes?: number | null
  isLive: boolean
  state: string
  error?: string | null
  lastCheckedAtUtc?: string | null
}

export interface BroadcastSession extends Entity
{
  streamerId: string
  roomId: number
  title: string
  coverUrl: string
  startedAtUtc: string
  endedAtUtc?: string | null
  lastMediaAtUtc: string
  state: string
  hasDanmu: boolean
  totalBytes: number
  error?: string | null
  isTrashed: boolean
  mediaRevision: number
}

export interface RecordingSegment extends Entity
{
  sessionId: string
  attemptId: string
  relativePath: string
  startOffsetMs: number
  durationMs: number
  committedBytes: number
  isClosed: boolean
  revision: number
  codec: string
}

export interface TimelineGap
{
  startMs: number
  endMs: number
}

export interface SessionDetail
{
  session: BroadcastSession
  segments: RecordingSegment[]
  durationMs: number
  gaps: TimelineGap[]
}

export interface PlaybackWindow
{
  url: string | null
  startOffsetMs: number
  endOffsetMs: number
  requestedOffsetMs: number
  isGap: boolean
  gapEndMs?: number | null
  durationMs: number
}

export type EventKind = 'Danmu' | 'SuperChat' | 'Gift' | 'Guard' | 'RedPocket'

export interface RecordedEvent
{
  schemaVersion: number
  sequence: number
  eventId: string
  kind: string
  command: string
  receivedAtUtc: string
  sourceTimeUtc?: string | null
  offsetMs: number
  userId: string
  userName: string
  text: string
  avatarUrl?: string | null
  amount?: number | null
  quantity: number
  transactionId?: string | null
  coinType?: string | null
  priceSource?: string | null
  payload?: Record<string, unknown> | null
}

export interface EventPage
{
  items: RecordedEvent[]
  nextCursor?: number | null
}

export interface MediaJob extends Entity
{
  ownerId: string
  kind: string
  sessionId: string
  title: string
  mode: 'Exact' | 'Fast'
  startMs: number
  endMs: number
  segmentIds: string[]
  outputRelativePaths: string[]
  state: string
  progress: number
  error?: string | null
  finishedAtUtc?: string | null
  attempt: number
  cancelRequested: boolean
}

export interface CreateJob
{
  kind: 'Clip' | 'Repair' | 'Merge'
  sessionId: string
  title?: string
  mode: 'Exact' | 'Fast'
  startMs: number
  endMs: number
  segmentIds?: string[]
}

export interface Clip extends Entity
{
  ownerId: string
  sessionId: string
  jobId: string
  title: string
  bytes: number
  startMs: number
  endMs: number
  expiresAtUtc: string
  isTrashed: boolean
}

export interface TrashEntry extends Entity
{
  ownerId: string
  kind: string
  entityId: string
  title: string
  bytes: number
  purgeAtUtc: string
  state: string
}

export interface StorageVolume
{
  id: string
  path: string
  totalBytes: number
  usedBytes: number
  trashBytes: number
  freeBytes: number
  cacheBytes: number
}

export interface Analytics
{
  observedAmount: number
  goldGiftValue: number
  protocolPaidAmount: number
  quotedAmount: number
  paidRevenue: number
  unknownAmountCount: number
  unknownGiftQuantity: number
  unidentifiedFinancialEvents: number
  giftQuantity: number
  activeUsers: number
  danmuCount: number
  giftCount: number
  superChatCount: number
  guardCount: number
  redPocketCount: number
  coverageGaps: number
  topUsers: { userId: string; userName: string; messages: number; paidAmount: number }[]
  buckets: { offsetMs: number; messages: number; paidAmount: number }[]
}
