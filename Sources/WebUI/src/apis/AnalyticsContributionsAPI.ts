import { query, request } from './APIBase'
import type { AnalyticsContributionPage, AnalyticsContributionQuery } from '@/models/analyticsContributions'

export const AnalyticsContributionsAPI = {
  get: (sessionId: string, options: AnalyticsContributionQuery, signal?: AbortSignal) => request<AnalyticsContributionPage>(`sessions/${encodeURIComponent(sessionId)}/analytics/contributions${query({ ...options })}`, { signal }),
}
