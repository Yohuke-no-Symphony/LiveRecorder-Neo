export type TaskKind = 'media' | 'ai' | 'transcode' | 'recording'
export type TaskScope = 'personal' | 'administration'

export interface TaskMetric
{
  key: string
  value?: number | null
  unit: string
  text?: string | null
}

export interface TaskStage
{
  id: string
  state: string
  substate?: string | null
  progress?: number | null
  startedAtUtc?: string | null
  finishedAtUtc?: string | null
  elapsedMs?: number | null
  error?: string | null
  metrics: TaskMetric[]
}

export interface TaskSummary
{
  id: string
  kind: TaskKind
  type: string
  sessionId: string
  title: string
  state: string
  origin: 'User' | 'Automatic' | 'Legacy'
  initiatorUserId?: string | null
  initiatorName?: string | null
  initiatorAvatarUrl?: string | null
  workerId?: string | null
  workerName?: string | null
  workerOnline?: boolean | null
  profileName?: string | null
  mode?: string | null
  progress: number
  attempt: number
  createdAtUtc: string
  finishedAtUtc?: string | null
  lastUpdatedAtUtc?: string | null
  error?: string | null
  cancelRequested: boolean
  canCancel: boolean
  canRetry: boolean
  canDelete: boolean
  canViewResults: boolean
  liveMarkerFileId?: string | null
  outputCount: number
  stages: TaskStage[]
}

export interface TaskQuery
{
  state?: string
  kind?: TaskKind
  type?: string
  origin?: string
  workerId?: string
  initiatorUserId?: string
  search?: string
  cursor?: string
  limit?: number
}

export interface TaskPage
{
  items: TaskSummary[]
  total: number
  nextCursor: string | null
}

export interface TaskClearPreview
{
  count: number
  completedBeforeUtc: string
}

export interface TaskFilterOption
{
  id: string
  name: string
}

export interface TaskFilters
{
  initiators: TaskFilterOption[]
  workers: TaskFilterOption[]
}
