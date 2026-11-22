export interface ServerLogEntry
{
  id: number
  timestampUtc: string
  level: string
  category: string
  message: string
  exceptionType: string | null
}

export type ServerLogResetReason = 'processRestarted' | 'bufferOverwritten'

export interface ServerLogPage
{
  entries: ServerLogEntry[]
  nextCursor: string
  hasMore: boolean
  resetReason: ServerLogResetReason | null
}
