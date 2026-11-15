import { request } from './APIBase'
import type { TranscodeJob, TranscodingWorker, WorkerConfiguration } from '@/models/transcoding'

const send = <T>(path: string, method: string, body?: unknown) => request<T>(`transcoding/${path}`, { method, body: body === undefined ? undefined : JSON.stringify(body) })

export const TranscodingAPI = {
  workers: (signal?: AbortSignal) => request<TranscodingWorker[]>('transcoding/workers', { signal }),
  createWorker: (name: string) => send<{ worker: TranscodingWorker; secret: string }>('workers', 'POST', { name }),
  updateWorker: (id: string, body: { name: string; enabled: boolean; configuration: WorkerConfiguration; expectedRevision: number }) => send<TranscodingWorker>(`workers/${encodeURIComponent(id)}`, 'PUT', body),
  rotateWorkerKey: (id: string) => send<{ secret: string }>(`workers/${encodeURIComponent(id)}/rotate-key`, 'POST'),
  deleteWorker: (id: string) => send<void>(`workers/${encodeURIComponent(id)}`, 'DELETE'),
  jobs: (signal?: AbortSignal, sessionId?: string) => request<TranscodeJob[]>(`transcoding/jobs${sessionId ? `?sessionId=${encodeURIComponent(sessionId)}` : ''}`, { signal }),
  createJobs: (sessionIds: string[], qualityIds?: string[], force = false) => send<{ queued: number }>('jobs', 'POST', { sessionIds, qualityIds, force }),
  cancelJob: (id: string) => send<void>(`jobs/${encodeURIComponent(id)}/cancel`, 'POST'),
  retryJob: (id: string) => send<void>(`jobs/${encodeURIComponent(id)}/retry`, 'POST'),
}
