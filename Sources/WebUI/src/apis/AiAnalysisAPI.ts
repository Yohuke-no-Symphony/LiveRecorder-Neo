import { post, request } from './APIBase'
import type { AiAnalysisJob, AiAnalysisStatus } from '@/models/aiAnalysis'

export const AiAnalysisAPI = {
  jobs: (signal?: AbortSignal) => request<AiAnalysisJob[]>('ai/jobs', { signal }),
  status: (sessionId: string, signal?: AbortSignal) => request<AiAnalysisStatus>(`sessions/${encodeURIComponent(sessionId)}/ai-analysis`, { signal }),
  generate: (sessionId: string, force = false) => post<AiAnalysisJob>(`sessions/${encodeURIComponent(sessionId)}/ai-analysis`, { force }),
  liveMarkerStatus: (sessionId: string, signal?: AbortSignal) => request<AiAnalysisStatus>(`sessions/${encodeURIComponent(sessionId)}/ai-live-markers`, { signal }),
  generateLiveMarkers: (sessionId: string, force = false) => post<AiAnalysisJob>(`sessions/${encodeURIComponent(sessionId)}/ai-live-markers`, { force }),
  cancel: (jobId: string) => post<void>(`ai/jobs/${encodeURIComponent(jobId)}/cancel`),
}
