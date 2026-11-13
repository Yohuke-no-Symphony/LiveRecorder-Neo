import { put, remove, request } from './APIBase'
import type { LiveMarkerDetail, LiveMarkerFile, LiveMarkerFileList, LiveMarkerUpload } from '@/models/liveMarkers'

function path(sessionId: string, fileId?: string): string
{
  const root = `sessions/${encodeURIComponent(sessionId)}/live-markers`
  return fileId === undefined ? root : `${root}/${encodeURIComponent(fileId)}`
}

export const LiveMarkerAPI = {
  files: (sessionId: string, signal?: AbortSignal) => request<LiveMarkerFileList>(path(sessionId), { signal }),
  detail: (sessionId: string, fileId: string, signal?: AbortSignal) => request<LiveMarkerDetail>(path(sessionId, fileId), { signal }),
  upload: (sessionId: string, value: LiveMarkerUpload) =>
  {
    const body = new FormData()
    body.set('file', value.file)
    body.set('timeZoneId', value.timeZoneId)
    body.set('calibrationMs', String(value.calibrationMs))
    return request<LiveMarkerFile>(path(sessionId), { method: 'POST', body })
  },
  align: (sessionId: string, fileId: string, calibrationMs: number) => put<LiveMarkerFile>(`${path(sessionId, fileId)}/alignment`, { calibrationMs }),
  remove: (sessionId: string, fileId: string) => remove(path(sessionId, fileId)),
}
