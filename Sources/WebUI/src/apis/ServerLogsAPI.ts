import { query, request } from './APIBase'
import type { ServerLogPage } from '@/models/serverLogs'

export const ServerLogsAPI = {
  read: (cursor: string | undefined, signal: AbortSignal) =>
    request<ServerLogPage>(`admin/server-logs${query({ cursor })}`, { signal, cache: 'no-store' }),
}
