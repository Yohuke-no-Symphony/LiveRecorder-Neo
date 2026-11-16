<template>
  <v-card class="event-panel">
    <CardHeader class="feed-heading" :title="t('components.events.title')">
      <template #actions><slot name="settings" /><v-btn class="feed-follow" :color="follow ? 'primary' : 'secondary'" variant="text" size="small" :prepend-icon="follow ? 'mdi-arrow-down-circle-outline' : 'mdi-pause-circle-outline'" @click="follow = !follow">{{ t('components.events.follow') }}</v-btn></template>
    </CardHeader>
    <v-chip-group v-model="kind" mandatory class="feed-tabs px-4" color="primary"><v-chip value="all" size="small">{{ t('components.events.all') }}</v-chip><v-chip v-for="item in kinds" :key="item.value" :value="item.value" size="small">{{ item.title }}</v-chip></v-chip-group>
    <v-alert v-if="error" type="error" class="mx-4 my-3" density="compact">{{ error }}</v-alert>
    <v-progress-linear v-if="loading" color="primary" height="2" indeterminate />
    <div v-if="visibleEvents.length" ref="feedViewport" class="event-scroll" :class="{ 'is-following': follow }" tabindex="0" @scroll.passive="onScroll" @wheel.passive="onWheel" @touchstart.passive="onTouchStart" @touchmove.passive="onTouchMove" @pointerdown="onPointerDown" @keydown="onKeydown">
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
  </v-card>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { MediaAPI } from '@/apis/MediaAPI'
import { usePolling } from '@/composables/usePolling'
import { usePlaybackEvents } from '@/composables/usePlaybackEvents'
import type { EventKind, RecordedEvent } from '@/models/media'
import { DEFAULT_PLAYBACK_EVENT_RETENTION_COUNT } from '@/models/playbackPreferences'
import EventMessageRow from '@/components/events/EventMessageRow.vue'
import CardHeader from '@/components/common/CardHeader.vue'

const { t } = useI18n()

const props = withDefaults(defineProps<{ sessionId: string; offsetMs: number; enabled: boolean; isLive: boolean; navigationRevision?: number; active?: boolean; retentionCount?: number }>(), { active: true, retentionCount: DEFAULT_PLAYBACK_EVENT_RETENTION_COUNT })
defineEmits<{ seek: [offsetMs: number] }>()
const kind = ref<EventKind | 'all'>('all')
const events = ref<RecordedEvent[]>([])
const error = ref('')
const windowLoading = ref(false)
const readingLoading = ref(false)
const loading = computed(() => windowLoading.value || readingLoading.value)
const follow = ref(true)
const feed = ref<{ scrollToIndex: (index: number, position: 'end') => void }>()
const feedViewport = ref<HTMLElement>()
const feedContent = ref<HTMLElement>()
const rangeStart = ref(-1)
const rangeEnd = ref(-1)
const pausedEvents = shallowRef<readonly RecordedEvent[]>()
const pausedOffset = ref(0)
const kinds = computed<{ title: string; value: EventKind }[]>(() => [
  { title: t('components.events.danmu'), value: 'Danmu' },
  { title: t('components.events.superChat'), value: 'SuperChat' },
  { title: t('components.events.gift'), value: 'Gift' },
  { title: t('components.events.guard'), value: 'Guard' },
  { title: t('components.events.redPocket'), value: 'RedPocket' },
])
const visibleEvents = usePlaybackEvents(() => pausedEvents.value ?? events.value, () => pausedEvents.value ? pausedOffset.value : props.offsetMs, kind, () => props.retentionCount)
let controller: AbortController | undefined
let generation = 0
let readingController: AbortController | undefined
let readingGeneration = 0
let touchY: number | undefined
let resumeOnScroll = false
let followFrame = 0
let loadedKind: EventKind | 'all' = 'all'
let loadedRetentionCount = 0
let pendingRequestKey: string | undefined
let pendingReadingRequestKey: string | undefined

async function load(force = false, refreshReading = false): Promise<void>
{
  const reading = refreshReading && pausedEvents.value !== undefined
  const offsetMs = reading ? pausedOffset.value : props.offsetMs
  const selectedKind = kind.value
  const retentionCount = props.retentionCount
  if (!props.active || !props.enabled || (!force && selectedKind === loadedKind && retentionCount <= loadedRetentionCount && offsetMs >= rangeStart.value && offsetMs < rangeEnd.value - 10000))
  {
    return
  }
  const requestKey = JSON.stringify([props.sessionId, offsetMs, selectedKind, retentionCount, reading])
  const requestLoading = reading ? readingLoading : windowLoading
  if (requestLoading.value && (!force || (reading ? pendingReadingRequestKey : pendingRequestKey) === requestKey))
  {
    return
  }
  const operation = reading ? ++readingGeneration : ++generation
  const requestController = new AbortController()
  if (reading)
  {
    readingController?.abort()
    readingController = requestController
    pendingReadingRequestKey = requestKey
  }
  else
  {
    controller?.abort()
    controller = requestController
    pendingRequestKey = requestKey
  }
  const signal = requestController.signal
  requestLoading.value = true
  error.value = ''
  try
  {
    const window = await MediaAPI.playbackEvents(props.sessionId, { offsetMs, kind: selectedKind === 'all' ? undefined : selectedKind, limit: retentionCount }, signal)
    if (signal.aborted || operation !== (reading ? readingGeneration : generation)) { return }
    const previous = new Map((reading ? pausedEvents.value ?? [] : events.value).map(item => [item.sequence, item]))
    const updated = window.items.map(item => previous.get(item.sequence) ?? item)
    if (reading)
    {
      if (!follow.value && pausedOffset.value === offsetMs)
      {
        pausedEvents.value = updated
      }
    }
    else
    {
      if (updated.length !== events.value.length || updated.some((item, index) => item !== events.value[index]))
      {
        events.value = updated
      }
      rangeStart.value = window.fromMs
      rangeEnd.value = window.toMs
      loadedKind = selectedKind
      loadedRetentionCount = retentionCount
    }
  }
  catch (value)
  {
    if (!signal.aborted && operation === (reading ? readingGeneration : generation))
    {
      error.value = value instanceof Error ? value.message : t('components.events.loadFailed')
    }
    return
  }
  finally
  {
    if (operation === (reading ? readingGeneration : generation))
    {
      requestLoading.value = false
      if (reading)
      {
        pendingReadingRequestKey = undefined
      }
      else
      {
        pendingRequestKey = undefined
      }
    }
  }
  if (!reading && !signal.aborted && operation === generation && props.offsetMs !== offsetMs)
  {
    void load()
  }
}
watch([() => props.offsetMs, () => props.navigationRevision ?? 0], ([offset, revision], [previousOffset, previousRevision]) =>
{
  const navigated = revision !== previousRevision
  if (navigated)
  {
    cancelReadingRequest()
    pausedEvents.value = undefined
    follow.value = true
  }
  if (navigated || Math.floor(offset / 5000) !== Math.floor(previousOffset / 5000) || offset < previousOffset && offset < rangeStart.value)
  {
    void load(navigated)
  }
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
    cancelReadingRequest()
    pausedEvents.value = undefined
    void load()
  }
  else
  {
    pausedOffset.value = props.offsetMs
    pausedEvents.value = events.value
  }
}, { flush: 'sync' })
function cancelReadingRequest(): void
{
  readingGeneration++
  readingController?.abort()
  readingLoading.value = false
  pendingReadingRequestKey = undefined
}
function cancelRequests(): void
{
  generation++
  controller?.abort()
  windowLoading.value = false
  pendingRequestKey = undefined
  cancelReadingRequest()
}
function resetEvents(): void
{
  cancelRequests()
  events.value = []
  pausedEvents.value = undefined
  rangeStart.value = -1
  rangeEnd.value = -1
  loadedRetentionCount = 0
  error.value = ''
}
watch([() => props.sessionId, () => props.enabled], () =>
{
  resetEvents()
  follow.value = true
  void load()
})
watch([kind, () => props.retentionCount], () => { void load(true, true) })
watch([() => visibleEvents.value.length, () => visibleEvents.value.at(-1)?.sequence, follow, () => props.active], () =>
{
  if (props.active && follow.value && visibleEvents.value.length)
  {
    feed.value?.scrollToIndex(visibleEvents.value.length - 1, 'end')
    scheduleFollowToBottom()
  }
}, { flush: 'post' })
watch([feedViewport, feedContent], ([viewport, content], _previous, onCleanup) =>
{
  if (!viewport || !content)
  {
    return
  }
  const observer = new ResizeObserver(scheduleFollowToBottom)
  observer.observe(content)
  observer.observe(viewport)
  onCleanup(() => observer.disconnect())
}, { flush: 'post' })
function scheduleFollowToBottom(): void
{
  if (!props.active || !follow.value || !visibleEvents.value.length || followFrame)
  {
    return
  }
  followFrame = requestAnimationFrame(() =>
  {
    followFrame = 0
    const viewport = feedViewport.value
    if (props.active && follow.value && visibleEvents.value.length && viewport)
    {
      viewport.scrollTop = viewport.scrollHeight
    }
  })
}
function onScroll(event: Event): void
{
  const element = event.target as HTMLElement
  const distance = element.scrollHeight - element.scrollTop - element.clientHeight
  if (follow.value)
  {
    if (Math.abs(distance) > 1)
    {
      scheduleFollowToBottom()
    }
  }
  else if (resumeOnScroll && distance <= 1)
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
watch(() => props.active, active =>
{
  if (active)
  {
    void load(true)
    if (pausedEvents.value)
    {
      void load(true, true)
    }
  }
  else
  {
    cancelRequests()
    if (followFrame)
    {
      cancelAnimationFrame(followFrame)
      followFrame = 0
    }
  }
})
usePolling(async () => { await load(props.isLive && !windowLoading.value) }, 10000)
onBeforeUnmount(() =>
{
  cancelRequests()
  if (followFrame)
  {
    cancelAnimationFrame(followFrame)
  }
})
</script>

<style scoped>
.event-panel { display: flex; flex-direction: column; min-height: 0; overflow: hidden; }
.feed-heading, .feed-tabs { flex-shrink: 0; }
.feed-tabs :deep(.v-slide-group__content) { gap: 4px; }.feed-tabs :deep(.v-chip) { font-size: .7rem; margin-inline: 0; }
.event-scroll { flex: 1; min-height: 0; position: relative; overflow-y: auto; overscroll-behavior-y: contain; }
.event-scroll.is-following { overflow-anchor: none; }
.event-list { min-height: 100%; display: flex; flex-direction: column; justify-content: flex-end; }
.event-row, .event-list :deep(.v-virtual-scroll__spacer) { flex-shrink: 0; }
.event-row { padding: 13px 16px; border-bottom: 1px solid rgba(var(--v-theme-on-surface), .045); }
.event-empty { flex: 1; min-height: 0; display: flex; flex-direction: column; gap: 15px; align-items: center; justify-content: center; font-size: .8rem; color: rgba(var(--v-theme-on-surface), .3); }
</style>
