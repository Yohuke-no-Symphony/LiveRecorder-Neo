import type { AiAnalysisJob } from '@/models/aiAnalysis'

export function liveMarkerLocation(sessionId: string, fileId?: string | null)
{
  return { path: `/sessions/${encodeURIComponent(sessionId)}`, query: { panel: 'markers', ...(fileId ? { liveMarkerFileId: fileId } : {}) } }
}

export function aiJobResultLocation(job: AiAnalysisJob)
{
  return job.purpose === 'LiveMarkers' ? liveMarkerLocation(job.sessionId, job.liveMarkerFileId)
    : { path: '/analytics', query: { sessionId: job.sessionId } }
}
