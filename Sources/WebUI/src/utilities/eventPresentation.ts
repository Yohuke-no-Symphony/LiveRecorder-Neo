import type { EventKind, RecordedEvent } from '@/models/media'
import { i18n } from '@/plugins/i18n'

export interface EmoticonToken
{
  text: string
  url?: string
  width?: number
  height?: number
}

export function objectValue(value: unknown): Record<string, unknown>
{
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
}

export function textValue(value: unknown): string
{
  return typeof value === 'string' ? value : ''
}

export function numberValue(value: unknown): number | undefined
{
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

export function safeImageUrl(value: unknown): string | undefined
{
  if (typeof value !== 'string' || !value.trim()) { return undefined }
  try
  {
    const url = new URL(value, typeof window === 'undefined' ? 'https://localhost/' : window.location.href)
    return ['https:', 'http:'].includes(url.protocol) ? value : undefined
  }
  catch { return undefined }
}

export function guardLevelName(level: unknown): string
{
  const key = ({ 1: 'governor', 2: 'admiral', 3: 'captain' } as Record<number, string>)[Number(level)]
  return key ? i18n.global.t(`components.events.${key}`) : ''
}

export function eventQuantity(event: RecordedEvent): number
{
  const quantity = numberValue(event.quantity) ?? numberValue(event.payload?.quantity) ?? 1
  return quantity > 0 ? Math.floor(quantity) : 1
}

export function tokenizeDanmu(content: string, value: unknown): EmoticonToken[]
{
  const emoticons = (Array.isArray(value) ? value : []).map(objectValue).map(item => ({
    text: textValue(item.text), url: safeImageUrl(item.url),
    width: numberValue(item.width), height: numberValue(item.height),
  })).filter(item => item.text && item.url).sort((a, b) => b.text.length - a.text.length)
  const tokens: EmoticonToken[] = []
  let position = 0
  while (position < content.length)
  {
    let found: typeof emoticons[number] | undefined
    let foundAt = content.length
    for (const emoticon of emoticons)
    {
      const index = content.indexOf(emoticon.text, position)
      if (index >= 0 && index < foundAt) { found = emoticon; foundAt = index }
    }
    if (!found) { tokens.push({ text: content.slice(position) }); break }
    if (foundAt > position) { tokens.push({ text: content.slice(position, foundAt) }) }
    tokens.push(found)
    position = foundAt + found.text.length
  }
  return tokens
}

export function redPocketData(event: RecordedEvent): Record<string, unknown>
{
  return objectValue(event.payload?.redPocket)
}

function mergeRecords(previous: unknown, current: unknown, key: (item: Record<string, unknown>) => string): Record<string, unknown>[]
{
  const merged = new Map<string, Record<string, unknown>>()
  for (const item of [...(Array.isArray(previous) ? previous : []), ...(Array.isArray(current) ? current : [])].map(objectValue))
  {
    const identity = key(item)
    const result = { ...merged.get(identity) }
    for (const [field, value] of Object.entries(item))
    {
      if (value !== undefined && value !== null && value !== '') { result[field] = value }
    }
    merged.set(identity, result)
  }
  return [...merged.values()]
}

function mergeRedPocket(previous: RecordedEvent, current: RecordedEvent): RecordedEvent
{
  const oldData = redPocketData(previous)
  const newData = redPocketData(current)
  const data = { ...oldData }
  const missingZero = new Set(['lotId', 'senderUid', 'startTime', 'endTime', 'currentTime'])
  for (const [key, value] of Object.entries(newData))
  {
    if (value !== null && value !== undefined && value !== '' && !(value === 0 && missingZero.has(key)) && !Array.isArray(value))
    {
      data[key] = value
    }
  }
  data.hasResult = oldData.hasResult === true || newData.hasResult === true
  data.hasDetails = oldData.hasDetails === true || newData.hasDetails === true
  data.awards = mergeRecords(oldData.awards, newData.awards, item => textValue(item.giftId) || textValue(item.name))
  data.winners = mergeRecords(oldData.winners, newData.hasResult === true ? newData.winners : [], item => `${item.uid ?? ''}:${item.giftId ?? ''}:${item.userName ?? ''}`)
  return {
    ...current, userName: current.userName || previous.userName, userId: current.userId || previous.userId,
    avatarUrl: current.avatarUrl || previous.avatarUrl, text: current.text || previous.text,
    payload: { ...previous.payload, ...current.payload, isDrawn: previous.payload?.isDrawn === true || current.payload?.isDrawn === true, redPocket: data },
  }
}

const DISPLAY_EVENT_KINDS = new Set(['Danmu', 'SuperChat', 'Gift', 'Guard', 'RedPocket', 'Gap'])

export function isDisplayEventKind(kind: string): boolean
{
  return DISPLAY_EVENT_KINDS.has(kind)
}

export function displayEvents(events: readonly RecordedEvent[], offsetMs: number, kind: EventKind | 'all' = 'all'): RecordedEvent[]
{
  const output: RecordedEvent[] = []
  const pockets = new Map<string, number>()
  for (const event of events.filter(item => isDisplayEventKind(item.kind) && item.offsetMs <= offsetMs && (kind === 'all' || item.kind === kind)).sort((a, b) => a.offsetMs - b.offsetMs || a.sequence - b.sequence))
  {
    if (event.kind !== 'RedPocket') { output.push(event); continue }
    const data = redPocketData(event)
    const lotId = event.transactionId || (data.lotId ? String(data.lotId) : '')
    const existing = lotId ? pockets.get(lotId) : undefined
    if (existing === undefined)
    {
      if (lotId) { pockets.set(lotId, output.length) }
      const hasResult = data.hasResult === true
      output.push({ ...event, payload: { ...event.payload, redPocket: { ...data, winners: hasResult && Array.isArray(data.winners) ? data.winners : [] } } })
    }
    else
    {
      output[existing] = mergeRedPocket(output[existing]!, event)
    }
  }
  return output.sort((a, b) => a.offsetMs - b.offsetMs || a.sequence - b.sequence)
}
