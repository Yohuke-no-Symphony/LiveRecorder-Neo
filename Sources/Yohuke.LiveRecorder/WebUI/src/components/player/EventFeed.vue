<template>
  <v-card class="event-panel">
    <div class="feed-heading"><h2>直播消息</h2><v-btn :color="follow ? 'primary' : 'secondary'" variant="text" size="small" :prepend-icon="follow ? 'mdi-arrow-down-circle-outline' : 'mdi-pause-circle-outline'" @click="follow = !follow">跟随</v-btn></div>
    <v-chip-group v-model="kind" mandatory class="feed-tabs px-4" color="primary"><v-chip value="all" size="small">全部</v-chip><v-chip v-for="item in kinds" :key="item.value" :value="item.value" size="small">{{ item.title }}</v-chip></v-chip-group>
    <v-alert v-if="error" type="error" class="mx-4 my-3" density="compact">{{ error }}</v-alert>
    <v-progress-linear v-if="loading" color="primary" height="2" indeterminate />
    <v-virtual-scroll v-if="visibleEvents.length" ref="feed" :items="visibleEvents" class="event-scroll" :item-height="78" height="500" @scroll.passive="onScroll">
      <template #default="{ item }">
        <div class="event-row" :class="`event-${item.kind.toLowerCase()}`">
          <div v-if="item.kind === 'Gap'" class="gap-message"><button class="event-time mono" type="button" @click="$emit('seek', item.offsetMs)">{{ formatDuration(item.offsetMs) }}</button><v-icon icon="mdi-video-off-outline" size="15" /><span>{{ item.text || '录制中断' }}</span></div>
          <template v-else><div class="d-flex align-center ga-2 mb-1">
            <button class="event-time mono" type="button" @click="$emit('seek', item.offsetMs)">{{ formatDuration(item.offsetMs) }}</button>
            <v-avatar size="22" color="surface-variant"><v-img v-if="safeImageUrl(item.avatarUrl)" :src="safeImageUrl(item.avatarUrl)" /><span v-else class="text-caption">{{ item.userName?.slice(0, 1) || '·' }}</span></v-avatar>
            <span class="event-user text-truncate">{{ item.userName || '访客' }}</span>
            <v-icon v-if="item.kind !== 'Danmu'" :icon="kindIcons[item.kind]" :color="kindColors[item.kind]" size="14" />
          </div>
          <div class="event-content"><DanmuContent v-if="item.kind === 'Danmu'" :event="item" /><ScContent v-else-if="item.kind === 'SuperChat'" :event="item" /><GiftContent v-else-if="item.kind === 'Gift'" :event="item" /><GuardContent v-else-if="item.kind === 'Guard'" :event="item" /><RedPocketContent v-else-if="item.kind === 'RedPocket'" :event="item" /></div></template>
        </div>
      </template>
    </v-virtual-scroll>
    <div v-else-if="!loading" class="event-empty"><v-icon icon="mdi-comment-text-outline" size="28" /><span>{{ enabled ? '当前时段暂无消息' : '此场录播未保存弹幕' }}</span></div>
    <div class="feed-footer mono">{{ formatDuration(offsetMs, true) }}<span>{{ visibleEvents.length }} 条</span></div>
  </v-card>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import { usePolling } from '@/composables/usePolling'
import type { EventKind, RecordedEvent } from '@/models/media'
import { formatDuration } from '@/utilities/format'
import { displayEvents, safeImageUrl } from '@/utilities/eventPresentation'
import DanmuContent from '@/components/events/DanmuContent.vue'
import ScContent from '@/components/events/ScContent.vue'
import GiftContent from '@/components/events/GiftContent.vue'
import GuardContent from '@/components/events/GuardContent.vue'
import RedPocketContent from '@/components/events/RedPocketContent.vue'

const props = defineProps<{ sessionId: string; offsetMs: number; enabled: boolean; isLive: boolean }>()
defineEmits<{ seek: [offsetMs: number] }>()
const kind = ref<EventKind | 'all'>('all')
const events = ref<RecordedEvent[]>([])
const pocketHistory = ref<RecordedEvent[]>([])
const error = ref('')
const loading = ref(false)
const follow = ref(true)
const feed = ref<{ scrollToIndex: (index: number) => void }>()
const kinds: { title: string; value: EventKind }[] = [{ title: '弹幕', value: 'Danmu' }, { title: 'SC', value: 'SuperChat' }, { title: '礼物', value: 'Gift' }, { title: '舰长', value: 'Guard' }, { title: '红包', value: 'RedPocket' }]
const kindIcons: Record<string, string> = { SuperChat: 'mdi-message-badge-outline', Gift: 'mdi-gift-outline', Guard: 'mdi-shield-crown-outline', RedPocket: 'mdi-red-envelope' }
const kindColors: Record<string, string> = { SuperChat: 'info', Gift: 'primary', Guard: 'warning', RedPocket: 'error' }
const visibleEvents = computed(() => displayEvents([...events.value.filter(item => item.kind !== 'RedPocket'), ...pocketHistory.value], props.offsetMs, kind.value).filter(item => item.offsetMs >= rangeStart.value))
let controller: AbortController | undefined
const rangeStart = ref(-1)
const rangeEnd = ref(-1)
let generation = 0

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
    const pockets = new Map(pocketHistory.value.map(item => [item.sequence, item]))
    for (const item of collected.filter(item => item.kind === 'RedPocket')) { pockets.set(item.sequence, item) }
    pocketHistory.value = [...pockets.values()]
    events.value = collected.sort((a, b) => a.offsetMs - b.offsetMs || a.sequence - b.sequence)
    rangeStart.value = fromMs
    rangeEnd.value = toMs
  }
  catch (value)
  {
    if (!signal.aborted && operation === generation)
    {
      error.value = value instanceof Error ? value.message : '消息加载失败'
    }
  }
  finally
  {
    if (operation === generation) { loading.value = false }
  }
}
watch(() => Math.floor(props.offsetMs / 5000), () => { void load() })
watch(() => [visibleEvents.value.length, visibleEvents.value.at(-1)?.sequence, follow.value], async () =>
{
  if (follow.value)
  {
    await nextTick()
    feed.value?.scrollToIndex(Math.max(0, visibleEvents.value.length - 1))
  }
})
function onScroll(event: Event): void
{
  const element = event.target as HTMLElement
  follow.value = element.scrollHeight - element.scrollTop - element.clientHeight < 90
}
usePolling(async () => { await load(props.isLive) }, 10000)
onBeforeUnmount(() => { generation++; controller?.abort() })
</script>

<style scoped>
.event-panel { display: flex; flex-direction: column; height: 100%; min-height: 450px; overflow: hidden; }
.feed-heading { display: flex; justify-content: space-between; align-items: center; padding: 14px 16px 5px 20px; }.feed-heading h2 { font-size: .94rem; font-weight: 600; }
.feed-tabs :deep(.v-slide-group__content) { gap: 4px; }.feed-tabs :deep(.v-chip) { font-size: .7rem; margin-inline: 0; }
.event-scroll { flex: 1; }.event-row { padding: 13px 18px; border-bottom: 1px solid rgba(var(--v-theme-on-surface), .045); }
.event-time { font-size: .62rem; color: rgba(var(--v-theme-on-surface), .33); min-width: 42px; text-align: left; }.event-user { font-size: .73rem; color: rgba(var(--v-theme-on-surface), .6); }.event-amount { font-size: .72rem; }
.event-content { font-size: .8rem; line-height: 1.65; overflow-wrap: anywhere; padding-left: 51px; }.gap-message { display: flex; align-items: center; gap: 8px; font-size: .72rem; color: rgba(var(--v-theme-on-surface), .4); }
.event-empty { flex: 1; min-height: 250px; display: flex; flex-direction: column; gap: 15px; align-items: center; justify-content: center; font-size: .8rem; color: rgba(var(--v-theme-on-surface), .3); }
.feed-footer { display: flex; justify-content: space-between; padding: 12px 18px; font-size: .65rem; color: rgba(var(--v-theme-on-surface), .32); border-top: 1px solid rgba(var(--v-theme-on-surface), .06); }
</style>
