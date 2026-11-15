import { download, query } from './APIBase'
import type { AnalyticsRange } from '@/models/analyticsRange'

export const AnalyticsExportAPI = {
  download: (sessionId: string, range?: AnalyticsRange, signal?: AbortSignal) => download(
    `sessions/${encodeURIComponent(sessionId)}/analytics/export${query({ fromMs: range?.fromMs, toMs: range?.toMs })}`,
    '直播分析.xlsx',
    { signal },
  ),
}
