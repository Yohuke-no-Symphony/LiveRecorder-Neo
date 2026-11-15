import { computed, toValue, type ComputedRef, type MaybeRefOrGetter } from 'vue'
import type { EventKind, RecordedEvent } from '@/models/media'
import { displayEvents, isDisplayEventKind } from '@/utilities/eventPresentation'
import { DEFAULT_PLAYBACK_EVENT_RETENTION_COUNT } from '@/models/playbackPreferences'

export function usePlaybackEvents(events: MaybeRefOrGetter<readonly RecordedEvent[]>, offsetMs: MaybeRefOrGetter<number>, kind: MaybeRefOrGetter<EventKind | 'all'>, retentionCount: MaybeRefOrGetter<number> = DEFAULT_PLAYBACK_EVENT_RETENTION_COUNT): ComputedRef<RecordedEvent[]>
{
  const eventSource = computed(() => toValue(events))
  const orderedEvents = computed(() =>
  {
    const selected = toValue(kind)
    return eventSource.value.filter(item => isDisplayEventKind(item.kind) && (selected === 'all' || item.kind === selected)).sort((a, b) => a.offsetMs - b.offsetMs || a.sequence - b.sequence)
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
  const reachedEvents = computed<RecordedEvent[]>((previous) =>
  {
    const source = orderedEvents.value
    const count = reachedCount.value
    if (previous && previous.length === count && previous.every((item, index) => item === source[index]))
    {
      return previous
    }
    return source.slice(0, count)
  })
  const presentedEvents = computed(() => displayEvents(reachedEvents.value, Number.POSITIVE_INFINITY))
  return computed<RecordedEvent[]>((previous) =>
  {
    const visible = presentedEvents.value.slice(-toValue(retentionCount))
    return previous && previous.length === visible.length && previous.every((item, index) => item === visible[index]) ? previous : visible
  })
}
