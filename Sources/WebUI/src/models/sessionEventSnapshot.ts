import type { RecordedEvent } from './media'

export interface SessionEventSnapshot
{
  version: string
  reset: boolean
  items: RecordedEvent[]
  translationSequences: number[]
  cursor: string
  isLive: boolean
  translationError?: string | null
}

export interface CachedSessionEvents
{
  version: string
  cursor: string
  items: RecordedEvent[]
  translationSequences: number[]
  translationError?: string | null
}
