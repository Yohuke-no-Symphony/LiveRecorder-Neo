export type AiJobState = 'Queued' | 'Running' | 'Completed' | 'Partial' | 'Cancelled' | 'Interrupted' | 'Failed'
export type AiJobPurpose = 'Analysis' | 'LiveMarkers'

export interface AiEvidence
{
  id: string
  offsetMs: number
  text: string
  kind: string
}

export interface AiTopic
{
  id: string
  title: string
  summary: string
  startMs: number
  endMs: number
  observations: string
  evidence: AiEvidence[]
}

export interface AiAnalysisResult
{
  purpose?: AiJobPurpose
  liveMarkerFileId?: string | null
  jobId: string
  sessionId: string
  sourceFingerprint: string
  model: string
  createdAtUtc: string
  completedAtUtc: string | null
  isComplete: boolean
  overview: string
  topics: AiTopic[]
}

export interface AiAnalysisJob
{
  id: string
  sessionId: string
  title: string
  cancelRequested: boolean
  purpose?: AiJobPurpose
  liveMarkerFileId?: string | null
  state: AiJobState
  progress: number
  phase?: 'Preparing' | 'Analyzing' | 'Summarizing' | 'Publishing' | 'Completed'
  totalSegments?: number
  completedSegments?: number
  currentSegment?: number | null
  currentSegmentStartMs?: number | null
  currentSegmentEndMs?: number | null
  contextCompactions?: number
  progressRecoveries?: number
  requestRetries?: number
  workerId?: string | null
  attempt?: number
  promptTokens: number
  completionTokens: number
  estimatedTokens: number
  tokenBudget: number
  modelRequests: number
  toolCalls: number
  errorCode: string | null
  error?: string | null
  createdAtUtc: string
  finishedAtUtc: string | null
}

export interface AiAnalysisStatus
{
  enabled: boolean
  canGenerate: boolean
  currentJob: AiAnalysisJob | null
  latestCompleted: AiAnalysisResult | null
  latestPartial: AiAnalysisResult | null
  stale: boolean
  latestCompletedStale?: boolean
  latestPartialStale?: boolean
}
