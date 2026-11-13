<template>
  <v-card class="event-panel">
    <div class="feed-heading"><h2>{{ t('components.events.title') }}</h2><v-btn :color="follow ? 'primary' : 'secondary'" variant="text" size="small" :prepend-icon="follow ? 'mdi-arrow-down-circle-outline' : 'mdi-pause-circle-outline'" @click="follow = !follow">{{ t('components.events.follow') }}</v-btn></div>
    <v-chip-group v-model="kind" mandatory class="feed-tabs px-4" color="primary"><v-chip value="all" size="small">{{ t('components.events.all') }}</v-chip><v-chip v-for="item in kinds" :key="item.value" :value="item.value" size="small">{{ item.title }}</v-chip></v-chip-group>
    <v-alert v-if="error" type="error" class="mx-4 my-3" density="compact">{{ error }}</v-alert>
    <v-progress-linear v-if="loading" color="primary" height="2" indeterminate />
    <div v-if="visibleEvents.length" ref="feedViewport" class="event-scroll" tabindex="0" @scroll.passive="onScroll" @wheel.passive="onWheel" @touchstart.passive="onTouchStart" @touchmove.passive="onTouchMove" @pointerdown="onPointerDown" @keydown="onKeydown">
      <div ref="feedContent" class="event-list">
        <v-virtual-scroll ref="feed" :items="visibleEvents" :item-height="78" item-key="sequence" renderless>
          <template #default="{ item, itemRef }">
            <div :ref="itemRef" class="event-row" :class="`event-${item.kind.toLowerCase()}`">
              <EventMessageRow :session-id="sessionId" :event="item" @seek="$emit('seek', $event)" />
            </div>
          </template>
        </v-virtual-scroll>
      </div>
    </div>
    <div v-else-if="!loading" class="event-empty"><v-icon icon="mdi-comment-text-outline" size="28" /><span>{{ enabled ? t('components.events.empty') : t('components.events.notRecorded') }}</span></div>
    <div class="feed-footer mono">{{ formatDuration(offsetMs, true) }}<span>{{ t('components.events.count', { count: formatNumber(visibleEvents.length) }) }}</span></div>
  </v-card>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { MediaAPI } from '@/apis/MediaAPI'
import { usePolling } from '@/composables/usePolling'
import { usePlaybackEvents } from '@/composables/usePlaybackEvents'
import type { EventKind, RecordedEvent } from '@/models/media'
import { formatDuration, formatNumber } from '@/utilities/format'
import EventMessageRow from '@/components/events/EventMessageRow.vue'

const { t } = useI18n()

const props = defineProps<{ sessionId: string; offsetMs: number; enabled: boolean; isLive: boolean; navigationRevision?: number }>()
defineEmits<{ seek: [offsetMs: number] }>()
const kind = ref<EventKind | 'all'>('all')
const events = ref<RecordedEvent[]>([])
const pocketHistory = ref<RecordedEvent[]>([])
const error = ref('')
const loading = ref(false)
const follow = ref(true)
const feed = ref<{ scrollToIndex: (index: number, position: 'end') => void }>()
const feedViewport = ref<HTMLElement>()
const feedContent = ref<HTMLElement>()
const rangeStart = ref(-1)
const rangeEnd = ref(-1)
const pausedEvents = shallowRef<readonly RecordedEvent[]>()
const pausedOffset = ref(0)
const pausedRangeStart = ref(0)
const kinds = computed<{ title: string; value: EventKind }[]>(() => [
  { title: t('components.events.danmu'), value: 'Danmu' },
  { title: t('components.events.superChat'), value: 'SuperChat' },
  { title: t('components.events.gift'), value: 'Gift' },
  { title: t('components.events.guard'), value: 'Guard' },
  { title: t('components.events.redPocket'), value: 'RedPocket' },
])
const sourceEvents = computed(() => [...events.value.filter(item => item.kind !== 'RedPocket'), ...pocketHistory.value])
const playbackEvents = usePlaybackEvents(() => pausedEvents.value ?? sourceEvents.value, () => pausedEvents.value ? pausedOffset.value : props.offsetMs, kind)
const visibleRangeStart = computed(() => pausedEvents.value ? pausedRangeStart.value : rangeStart.value)
const visibleEvents = computed<RecordedEvent[]>((previous) =>
{
  const visible = playbackEvents.value.filter(item => item.offsetMs >= visibleRangeStart.value)
  return previous && previous.length === visible.length && previous.every((item, index) => item === visible[index]) ? previous : visible
})
let controller: AbortController | undefined
let generation = 0
let touchY: number | undefined
let resumeOnScroll = false
let followFrame = 0

async function load(force = false): Promise<void>
{
  if (!props.enabled || (!force && props.offsetMs >= rangeStart.value && props.offsetMs < rangeEnd.value - 10000))
  {
    return
  }
  const operation = ++generation
  controller?.abort()
  controller = new AbortController()
  const signal = controller.signal
  const fromMs = Math.max(0, props.offsetMs - 120000)
  const toMs = props.offsetMs + 30000
  loading.value = true
  error.value = ''
  try
  {
    const collected: RecordedEvent[] = []
    let cursor: number | undefined
    do
    {
      const page = await MediaAPI.events(props.sessionId, { fromMs, toMs, cursor, limit: 500 }, signal)
      if (signal.aborted || operation !== generation) { return }
      collected.push(...page.items)
      const next = page.nextCursor ?? undefined
      if (next === cursor) { break }
      cursor = next
    } while (cursor)
    if (operation !== generation) { return }
    const previous = new Map([...events.value, ...pocketHistory.value].map(item => [item.sequence, item]))
    const updated = collected.map(item => previous.get(item.sequence) ?? item).sort((a, b) => a.offsetMs - b.offsetMs || a.sequence - b.sequence)
    const pockets = new Map(pocketHistory.value.map(item => [item.sequence, item]))
    for (const item of updated)
    {
      if (item.kind === 'RedPocket')
      {
        pockets.set(item.sequence, item)
      }
    }
    if (pockets.size !== pocketHistory.value.length)
    {
      pocketHistory.value = [...pockets.values()]
    }
    if (updated.length !== events.value.length || updated.some((item, index) => item !== events.value[index]))
    {
      events.value = updated
    }
    rangeStart.value = fromMs
    rangeEnd.value = toMs
  }
  catch (value)
  {
    if (!signal.aborted && operation === generation)
    {
      error.value = value instanceof Error ? value.message : t('components.events.loadFailed')
    }
  }
  finally
  {
    if (operation === generation) { loading.value = false }
  }
}
watch([() => Math.floor(props.offsetMs / 5000), () => props.navigationRevision ?? 0], ([_bucket, revision], [_previousBucket, previousRevision]) =>
{
  const navigated = revision !== previousRevision
  if (navigated)
  {
    follow.value = true
  }
  void load(navigated)
})
watch(follow, enabled =>
{
  resumeOnScroll = false
  if (followFrame)
  {
    cancelAnimationFrame(followFrame)
    followFrame = 0
  }
  if (enabled)
  {
    pausedEvents.value = undefined
  }
  else
  {
    pausedOffset.value = props.offsetMs
    pausedRangeStart.value = rangeStart.value
    pausedEvents.value = sourceEvents.value
  }
}, { flush: 'sync' })
watch([() => visibleEvents.value.length, () => visibleEvents.value.at(-1)?.sequence, follow], () =>
{
  if (follow.value && visibleEvents.value.length)
  {
    feed.value?.scrollToIndex(visibleEvents.value.length - 1, 'end')
  }
}, { flush: 'post' })
watch([feedViewport, feedContent], ([viewport, content], _previous, onCleanup) =>
{
  if (!viewport || !content)
  {
    return
  }
  const observer = new ResizeObserver(() =>
  {
    if (!follow.value || !visibleEvents.value.length || followFrame || viewport.scrollHeight - viewport.scrollTop - viewport.clientHeight <= 1)
    {
      return
    }
    followFrame = requestAnimationFrame(() =>
    {
      followFrame = 0
      if (follow.value && visibleEvents.value.length)
      {
        feed.value?.scrollToIndex(visibleEvents.value.length - 1, 'end')
      }
    })
  })
  observer.observe(content)
  observer.observe(viewport)
  onCleanup(() => observer.disconnect())
}, { flush: 'post' })
function onScroll(event: Event): void
{
  const element = event.target as HTMLElement
  if (!follow.value && resumeOnScroll && element.scrollHeight - element.scrollTop - element.clientHeight <= 1)
  {
    follow.value = true
  }
}
function onWheel(event: WheelEvent): void
{
  if (event.ctrlKey)
  {
    return
  }
  if (event.deltaY < 0)
  {
    follow.value = false
    resumeOnScroll = false
  }
  else if (event.deltaY > 0)
  {
    resumeOnScroll = true
  }
}
function onTouchStart(event: TouchEvent): void
{
  touchY = event.touches[0]?.clientY
}
function onTouchMove(event: TouchEvent): void
{
  const nextY = event.touches[0]?.clientY
  if (nextY !== undefined && touchY !== undefined && nextY > touchY)
  {
    follow.value = false
    resumeOnScroll = false
  }
  else if (nextY !== undefined && touchY !== undefined && nextY < touchY)
  {
    resumeOnScroll = true
  }
  touchY = nextY
}
function onPointerDown(event: PointerEvent): void
{
  if (event.target === event.currentTarget)
  {
    follow.value = false
    resumeOnScroll = true
  }
}
function onKeydown(event: KeyboardEvent): void
{
  if (['ArrowUp', 'PageUp', 'Home'].includes(event.key) || (event.key === ' ' && event.shiftKey))
  {
    follow.value = false
    resumeOnScroll = false
  }
  else if (['ArrowDown', 'PageDown', 'End'].includes(event.key) || (event.key === ' ' && !event.shiftKey))
  {
    resumeOnScroll = true
  }
}
usePolling(async () => { await load(props.isLive) }, 10000)
onBeforeUnmount(() =>
{
  generation++
  controller?.abort()
  if (followFrame)
  {
    cancelAnimationFrame(followFrame)
  }
})
</script>

<style scoped>
.event-panel { display: flex; flex-direction: column; min-height: 0; overflow: hidden; }
.feed-heading, .feed-tabs, .feed-footer { flex-shrink: 0; }
.feed-heading { display: flex; justify-content: space-between; align-items: center; padding: 14px 16px 5px 20px; }.feed-heading h2 { font-size: .94rem; font-weight: 600; }
.feed-tabs :deep(.v-slide-group__content) { gap: 4px; }.feed-tabs :deep(.v-chip) { font-size: .7rem; margin-inline: 0; }
.event-scroll { flex: 1; min-height: 0; position: relative; overflow-y: auto; overscroll-behavior-y: contain; }
.event-list { min-height: 100%; }.event-row { padding: 13px 16px; border-bottom: 1px solid rgba(var(--v-theme-on-surface), .045); }
.event-empty { flex: 1; min-height: 0; display: flex; flex-direction: column; gap: 15px; align-items: center; justify-content: center; font-size: .8rem; color: rgba(var(--v-theme-on-surface), .3); }
.feed-footer { display: flex; justify-content: space-between; padding: 12px 18px; font-size: .65rem; color: rgba(var(--v-theme-on-surface), .32); border-top: 1px solid rgba(var(--v-theme-on-surface), .06); }
</style>
