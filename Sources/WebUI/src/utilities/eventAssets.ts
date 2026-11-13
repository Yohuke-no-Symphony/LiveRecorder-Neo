import type { RecordedEvent } from '@/models/media'
import { getServerUrl } from '@/apis/ServerURL'
import { objectValue, safeImageUrl, textValue, tokenizeDanmu, type EmoticonToken } from '@/utilities/eventPresentation'

export interface ArchivedEmoticonToken extends EmoticonToken
{
  emoticonIndex?: number
}

export function audienceAvatarUrl(value: unknown): string | undefined
{
  const userId = typeof value === 'string' ? value : typeof value === 'number' && Number.isSafeInteger(value) ? String(value) : ''
  if (userId.length > 19 || !/^[1-9]\d*$/.test(userId) || BigInt(userId) > 9223372036854775807n)
  {
    return undefined
  }
  return getServerUrl(`/api/audience/${userId}/avatar`)
}

export function eventAvatarSources(sessionId: string, event: RecordedEvent): string[]
{
  const sources: string[] = []
  if (sessionId && Number.isSafeInteger(event.sequence) && event.sequence > 0 && safeImageUrl(event.avatarUrl))
  {
    sources.push(getServerUrl(`/api/sessions/${encodeURIComponent(sessionId)}/events/${event.sequence}/avatar`))
  }
  const audience = audienceAvatarUrl(event.userId)
  if (audience)
  {
    sources.push(audience)
  }
  return sources
}

export function eventEmoticonUrl(sessionId: string | undefined, sequence: number | undefined, index: number | undefined): string | undefined
{
  if (!sessionId || sequence === undefined || !Number.isSafeInteger(sequence) || sequence <= 0 || index === undefined || !Number.isSafeInteger(index) || index < 0)
  {
    return undefined
  }
  return getServerUrl(`/api/sessions/${encodeURIComponent(sessionId)}/events/${sequence}/emoticons/${index}`)
}

export function tokenizeEventEmoticons(content: string, value: unknown): ArchivedEmoticonToken[]
{
  const original = Array.isArray(value) ? value.map(objectValue) : []
  return tokenizeDanmu(content, value).map(token =>
  {
    if (!token.url)
    {
      return token
    }
    const emoticonIndex = original.findIndex(item => textValue(item.text) === token.text && safeImageUrl(item.url) === token.url)
    return { ...token, emoticonIndex: emoticonIndex >= 0 ? emoticonIndex : undefined }
  })
}
