import { computed, toValue, type ComputedRef, type MaybeRefOrGetter } from 'vue'
import type { EventKind, RecordedEvent } from '@/models/media'
import { displayEvents, isDisplayEventKind, mergeRedPocket, redPocketData } from '@/utilities/eventPresentation'
import { DEFAULT_PLAYBACK_EVENT_RETENTION_COUNT } from '@/models/playbackPreferences'

export function usePlaybackEvents(events: MaybeRefOrGetter<readonly RecordedEvent[]>, offsetMs: MaybeRefOrGetter<number>, kind: MaybeRefOrGetter<EventKind | 'all'>, retentionCount: MaybeRefOrGetter<number> = DEFAULT_PLAYBACK_EVENT_RETENTION_COUNT): ComputedRef<RecordedEvent[]>
{
  const eventSource = computed(() => toValue(events))
  function identity(item: RecordedEvent): string | number
  {
    const lotId = item.kind === 'RedPocket' ? item.transactionId || redPocketData(item).lotId : undefined
    return lotId ? `RedPocket:${lotId}` : item.sequence
  }
  const orderedEvents = computed(() =>
  {
    const selected = toValue(kind)
    const ordered = eventSource.value.filter(item => isDisplayEventKind(item.kind) && (selected === 'all' || item.kind === selected))
    const compare = (left: RecordedEvent, right: RecordedEvent) => left.offsetMs - right.offsetMs || left.sequence - right.sequence
    if (ordered.some((item, index) => index > 0 && compare(ordered[index - 1]!, item) > 0))
    {
      ordered.sort(compare)
    }
    const pockets = new Map<string | number, RecordedEvent>()
    return ordered.map(item =>
    {
      if (item.kind !== 'RedPocket')
      {
        return item
      }
      const key = identity(item)
      const previous = pockets.get(key)
      const presented = previous ? mergeRedPocket(previous, item) : displayEvents([item], Infinity)[0]!
      pockets.set(key, presented)
      return presented
    })
  })
  const reachedCount = computed(() =>
  {
    const source = orderedEvents.value
    const offset = toValue(offsetMs)
    let start = 0
    let end = source.length
    while (start < end)
    {
      const middle = Math.floor((start + end) / 2)
      if (source[middle]!.offsetMs <= offset)
      {
        start = middle + 1
      }
      else
      {
        end = middle
      }
    }
    return start
  })
  return computed<RecordedEvent[]>((previous) =>
  {
    const source = orderedEvents.value
    const count = reachedCount.value
    const visible: RecordedEvent[] = []
    const selected = new Set<string | number>()
    const limit = toValue(retentionCount)
    for (let index = count - 1; index >= 0 && visible.length < limit; index--)
    {
      const item = source[index]!
      const key = identity(item)
      if (!selected.has(key))
      {
        selected.add(key)
        visible.push(item)
      }
    }
    visible.reverse()
    return previous && previous.length === visible.length && previous.every((item, index) => item === visible[index]) ? previous : visible
  })
}
