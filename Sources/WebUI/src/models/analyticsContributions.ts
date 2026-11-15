import type { RecordedEvent } from './media'

export type ContributionKind = 'SuperChat' | 'Gift' | 'Guard' | 'RedPocket'
export type ContributionFilter = ContributionKind | 'All'
export type ContributionView = 'events' | 'users'

export interface AnalyticsContribution
{
  id: string
  kind: ContributionFilter
  userId: string
  userName: string
  avatarUrl: string | null
  offsetMs: number
  sequence: number
  text: string
  giftId: string
  giftName: string
  guardLevel: number | null
  quantity: number
  count: number
  amount: number | null
  knownAmount: number
  unknownAmountCount: number
  quotedAmount: number | null
  coinType: string | null
  priceSource: string
  totalBattery: number | null
  event: RecordedEvent | null
  categories: AnalyticsContributionCategory[]
}

export interface AnalyticsContributionCategory
{
  kind: ContributionKind
  count: number
  quantity: number
  amount: number | null
  knownAmount: number
  quotedAmount: number
  unknownAmountCount: number
  totalBattery: number | null
  projects: AnalyticsContributionProject[]
}

export interface AnalyticsContributionProject
{
  giftId: string
  giftName: string
  guardLevel: number | null
  text: string
  count: number
  quantity: number
  amount: number | null
  knownAmount: number
  quotedAmount: number
  unknownAmountCount: number
}

export interface AnalyticsContributionSummary
{
  count: number
  users: number
  quantity: number
  amount: number
  quotedAmount: number
  unknownAmountCount: number
  totalBattery: number | null
}

export interface AnalyticsContributionPage
{
  snapshotId: string
  kind: ContributionFilter
  view: ContributionView
  fromMs: number
  toMs: number
  items: AnalyticsContribution[]
  nextCursor: number | null
  totalCount: number
  summary: AnalyticsContributionSummary
}

export interface AnalyticsContributionQuery
{
  kind: ContributionFilter
  view: ContributionView
  fromMs?: number
  toMs?: number
  cursor?: number
  snapshotId?: string
  limit?: number
}
