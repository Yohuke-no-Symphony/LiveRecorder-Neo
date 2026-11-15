export const DEFAULT_PLAYBACK_EVENT_RETENTION_COUNT = 500
export const MAX_PLAYBACK_EVENT_RETENTION_COUNT = 2147483647

export function isPlaybackEventRetentionCount(value: unknown): value is number
{
  return typeof value === 'number' && Number.isInteger(value) && value > 0 && value <= MAX_PLAYBACK_EVENT_RETENTION_COUNT
}
