import { put, request } from './APIBase'
import type { SessionConfiguration } from '@/models/sessionConfiguration'

export const SessionConfigurationAPI = {
  get: (id: string) => request<SessionConfiguration>(`sessions/${encodeURIComponent(id)}/configuration`),
  update: (id: string, translationDanmuPatternOverride: string | null, expectedRevision: number) => put<SessionConfiguration>(`sessions/${encodeURIComponent(id)}/configuration`, { translationDanmuPatternOverride, expectedRevision }),
}
