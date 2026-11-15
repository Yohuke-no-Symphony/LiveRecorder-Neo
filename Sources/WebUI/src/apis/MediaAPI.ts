import { post, put, query, remove, request } from './APIBase'
import { getServerUrl } from './ServerURL'
import type { SessionTimeline } from '@/models/sessionTimeline'
import type { SessionBookmark } from '@/models/sessionBookmark'
import type { SessionTrashResult } from '@/models/sessionLibrary'
import type { SessionGroupConfiguration } from '@/models/sessionConfiguration'
import type { Analytics, BroadcastSession, Clip, CreateJob, EventKind, EventPage, MediaJob, PlaybackWindow, SessionDetail, StorageVolume, Streamer, TrashEntry } from '@/models/media'
import type { ServerPaths, ServerSettings } from '@/models/settings'
import type { StorageUsageReport } from '@/models/storage'
import type { PlaybackLoadOptions } from '@/models/playbackLoadOptions'

export const MediaAPI = {
  streamers: () => request<Streamer[]>('streamers'),
  createStreamer: (body: { roomId: number; accountId?: string | null; enabled?: boolean }) => post<Streamer>('streamers', body),
  updateStreamer: (id: string, body: { enabled: boolean; accountId?: string | null; quality?: number | null; recordDanmu?: boolean | null; segmentBytes?: number | null }) => put<Streamer>(`streamers/${encodeURIComponent(id)}`, body),
  deleteStreamer: (id: string) => remove(`streamers/${encodeURIComponent(id)}`),
  avatar: (id: string) => getServerUrl(`/api/streamers/${encodeURIComponent(id)}/avatar`),
  sessions: (streamerId?: string, search?: string, before?: string, take = 50) => request<BroadcastSession[]>(`sessions${query({ streamerId, search, before, take })}`),
  session: (id: string) => request<SessionDetail>(`sessions/${encodeURIComponent(id)}`),
  setSessionPermanent: (id: string, isPermanent: boolean) => put<BroadcastSession>(`sessions/${encodeURIComponent(id)}/retention`, { isPermanent }),
  deleteSession: (id: string) => remove(`sessions/${encodeURIComponent(id)}`),
  deleteSegment: (id: string, segmentId: string) => remove(`sessions/${encodeURIComponent(id)}/segments/${encodeURIComponent(segmentId)}`),
  trashSessions: (sessionIds: string[]) => post<SessionTrashResult[]>('sessions/trash', { sessionIds }),
  groupSessions: (id: string, sessionIds: string[], title?: string, configuration?: SessionGroupConfiguration) => post<BroadcastSession>(`sessions/${encodeURIComponent(id)}/group`, { sessionIds, title, configuration }),
  splitSession: (id: string, beforeSegmentId: string) => post<{ sessions: BroadcastSession[] }>(`sessions/${encodeURIComponent(id)}/split`, { beforeSegmentId }),
  cover: (id: string) => getServerUrl(`/api/sessions/${encodeURIComponent(id)}/cover`),
  downloadSegment: (id: string, segmentId: string) => getServerUrl(`/api/sessions/${encodeURIComponent(id)}/segments/${encodeURIComponent(segmentId)}/download`),
  playback: (id: string, offsetMs: number, options: PlaybackLoadOptions = {}) => request<PlaybackWindow | undefined>(`sessions/${encodeURIComponent(id)}/playback`, { method: 'POST', body: JSON.stringify({ offsetMs, prefetch: options.prefetch, qualityId: options.qualityId }), signal: options.signal }),
  bookmarks: (id: string, signal?: AbortSignal) => request<SessionBookmark[]>(`sessions/${encodeURIComponent(id)}/bookmarks`, { signal }),
  createBookmark: (id: string, body: { title: string; offsetMs: number }) => post<SessionBookmark>(`sessions/${encodeURIComponent(id)}/bookmarks`, body),
  updateBookmark: (id: string, bookmarkId: string, body: { title: string; offsetMs: number }) => put<SessionBookmark>(`sessions/${encodeURIComponent(id)}/bookmarks/${encodeURIComponent(bookmarkId)}`, body),
  deleteBookmark: (id: string, bookmarkId: string) => remove(`sessions/${encodeURIComponent(id)}/bookmarks/${encodeURIComponent(bookmarkId)}`),
  events: (id: string, options: { fromMs: number; toMs: number; kind?: EventKind; cursor?: number; limit?: number; translationsOnly?: boolean }, signal?: AbortSignal) => request<EventPage>(`sessions/${encodeURIComponent(id)}/events${query(options)}`, { signal }),
  jobs: (signal?: AbortSignal) => request<MediaJob[]>('jobs', { signal }),
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
  storage: (refresh = false) => request<StorageVolume[]>(`storage${query({ refresh })}`),
  storageDetails: (refresh = false) => request<StorageUsageReport>(`storage/details${query({ refresh })}`),
  settings: () => request<ServerSettings>('settings'),
  settingsPaths: () => request<ServerPaths>('settings/paths'),
  updateSettingsPaths: (body: { dataDirectory: string; recordsDirectory: string }) => put<ServerPaths>('settings/paths', body),
  updateSettings: (body: ServerSettings) => put<ServerSettings>('settings', body),
  analytics: (id: string, range?: { fromMs: number; toMs: number }, signal?: AbortSignal) => request<Analytics>(`sessions/${encodeURIComponent(id)}/analytics${query(range ?? {})}`, { signal }),
  timeline: (id: string, bucketCount = 300, signal?: AbortSignal) => request<SessionTimeline>(`sessions/${encodeURIComponent(id)}/timeline${query({ bucketCount })}`, { signal }),
}
