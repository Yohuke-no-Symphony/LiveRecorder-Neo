import { query, request } from './APIBase'
import type { RecordingWorkerAssignment, TranscodeJob, Worker, WorkerConfiguration, WorkerKind, WorkerLogPage } from '@/models/transcoding'

const send = <T>(path: string, method: string, body?: unknown) => request<T>(`transcoding/${path}`, { method, body: body === undefined ? undefined : JSON.stringify(body) })

export const TranscodingAPI = {
  workerAssignments: (id: string, signal?: AbortSignal) => request<RecordingWorkerAssignment[]>(`transcoding/workers/${encodeURIComponent(id)}/assignments`, { signal }),
  workers: (signal?: AbortSignal) => request<Worker[]>('transcoding/workers', { signal }),
  workerLogs: (id: string, after?: number, signal?: AbortSignal) => request<WorkerLogPage>(`transcoding/workers/${encodeURIComponent(id)}/logs${query({ after, take: 200 })}`, { signal }),
  createWorker: (name: string, kind: WorkerKind = 'Transcoding') => send<{ worker: Worker; secret: string }>('workers', 'POST', kind === 'Transcoding' ? { name } : { name, kind }),
  updateWorker: (id: string, body: { name: string; enabled: boolean; configuration: WorkerConfiguration; expectedRevision: number }) => send<Worker>(`workers/${encodeURIComponent(id)}`, 'PUT', body),
  rotateWorkerKey: (id: string) => send<{ secret: string }>(`workers/${encodeURIComponent(id)}/rotate-key`, 'POST'),
  deleteWorker: (id: string) => send<void>(`workers/${encodeURIComponent(id)}`, 'DELETE'),
  jobs: (signal?: AbortSignal, sessionId?: string) => request<TranscodeJob[]>(`transcoding/jobs${sessionId ? `?sessionId=${encodeURIComponent(sessionId)}` : ''}`, { signal }),
  createJobs: (sessionIds: string[], qualityIds?: string[], force = false) => send<{ queued: number }>('jobs', 'POST', { sessionIds, qualityIds, force }),
  cancelJob: (id: string) => send<void>(`jobs/${encodeURIComponent(id)}/cancel`, 'POST'),
  retryJob: (id: string) => send<void>(`jobs/${encodeURIComponent(id)}/retry`, 'POST'),
}
