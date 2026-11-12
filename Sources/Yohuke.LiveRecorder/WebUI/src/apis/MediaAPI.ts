import { post, put, query, remove, request } from './APIBase'
import { getServerUrl } from './ServerURL'
import type { Analytics, BroadcastSession, Clip, CreateJob, EventKind, EventPage, MediaJob, PlaybackWindow, SessionDetail, StorageVolume, Streamer, TrashEntry } from '@/models/media'
import type { ServerSettings } from '@/models/settings'

export const MediaAPI = {
  streamers: () => request<Streamer[]>('streamers'),
  createStreamer: (body: { roomId: number; accountId?: string | null; enabled?: boolean }) => post<Streamer>('streamers', body),
  updateStreamer: (id: string, body: { enabled: boolean; accountId?: string | null; quality?: number | null; recordDanmu?: boolean | null; segmentBytes?: number | null }) => put<Streamer>(`streamers/${encodeURIComponent(id)}`, body),
  deleteStreamer: (id: string) => remove(`streamers/${encodeURIComponent(id)}`),
  avatar: (id: string) => getServerUrl(`/api/streamers/${encodeURIComponent(id)}/avatar`),
  sessions: (streamerId?: string, search?: string, before?: string, take = 50) => request<BroadcastSession[]>(`sessions${query({ streamerId, search, before, take })}`),
  session: (id: string) => request<SessionDetail>(`sessions/${encodeURIComponent(id)}`),
  groupSessions: (id: string, sessionIds: string[], title?: string) => post<BroadcastSession>(`sessions/${encodeURIComponent(id)}/group`, { sessionIds, title }),
  splitSession: (id: string, beforeSegmentId: string) => post<{ sessions: BroadcastSession[] }>(`sessions/${encodeURIComponent(id)}/split`, { beforeSegmentId }),
  cover: (id: string) => getServerUrl(`/api/sessions/${encodeURIComponent(id)}/cover`),
  playback: (id: string, offsetMs: number) => post<PlaybackWindow>(`sessions/${encodeURIComponent(id)}/playback`, { offsetMs }),
  events: (id: string, options: { fromMs: number; toMs: number; kind?: EventKind; cursor?: number; limit?: number }, signal?: AbortSignal) => request<EventPage>(`sessions/${encodeURIComponent(id)}/events${query(options)}`, { signal }),
  jobs: () => request<MediaJob[]>('jobs'),
  createJob: (body: CreateJob) => post<MediaJob>('jobs', body),
  cancelJob: (id: string) => post<void>(`jobs/${encodeURIComponent(id)}/cancel`),
  retryJob: (id: string) => post<void>(`jobs/${encodeURIComponent(id)}/retry`),
  downloadJobOutput: (id: string, index: number) => getServerUrl(`/api/jobs/${encodeURIComponent(id)}/outputs/${index}`),
  clips: () => request<Clip[]>('clips'),
  deleteClip: (id: string) => remove(`clips/${encodeURIComponent(id)}`),
  downloadClip: (id: string) => getServerUrl(`/api/clips/${encodeURIComponent(id)}/download`),
  previewClip: (id: string) => getServerUrl(`/api/clips/${encodeURIComponent(id)}/media`),
  trash: () => request<TrashEntry[]>('trash'),
  restoreTrash: (id: string) => post<void>(`trash/${encodeURIComponent(id)}/restore`),
  purgeTrash: (id: string) => remove(`trash/${encodeURIComponent(id)}`),
  storage: () => request<StorageVolume[]>('storage'),
  settings: () => request<ServerSettings>('settings'),
  updateSettings: (body: ServerSettings) => put<ServerSettings>('settings', body),
  analytics: (id: string) => request<Analytics>(`sessions/${encodeURIComponent(id)}/analytics`),
}
