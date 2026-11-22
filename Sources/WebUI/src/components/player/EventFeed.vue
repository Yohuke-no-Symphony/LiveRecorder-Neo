<template>
  <v-card class="event-panel">
    <CardHeader class="feed-heading" :title="t('components.events.title')">
      <template #actions>
        <v-btn v-if="!searchOpen" icon="mdi-magnify" variant="text" size="small"
               :aria-label="t('components.events.search')" :title="t('components.events.search')"
               @click="searchOpen = true"/>
        <v-btn
          class="feed-follow"
          :icon="isFollowing ? 'mdi-arrow-down-circle-outline' : 'mdi-pause-circle-outline'"
          :active="isFollowing"
          :color="isFollowing ? 'primary' : 'secondary'"
          variant="text"
          size="small"
          :disabled="searchOpen"
          :aria-label="t('components.events.follow')"
          :aria-pressed="isFollowing"
          :title="t('components.events.follow')"
          @click="follow = !follow"
        />
        <v-btn class="feed-refresh" icon="mdi-refresh" variant="text" size="small" :loading="loading"
               :disabled="!enabled" :aria-label="t('views.common.refresh')" @click="refresh()"/>
        <slot name="settings"/>
      </template>
    </CardHeader>
    <PlaybackListSearch v-model="searchText" :open="searchOpen" :label="t('components.events.search')"
                        @close="closeSearch"/>
    <v-chip-group v-model="kind" mandatory class="feed-tabs px-4" color="primary">
      <v-chip value="all" size="small">{{ t('components.events.all') }}</v-chip>
      <v-chip v-for="item in kinds" :key="item.value" :value="item.value" size="small">{{ item.title }}</v-chip>
    </v-chip-group>
    <v-alert v-if="error" type="error" class="mx-4 my-3" density="compact">{{ error }}</v-alert>
    <v-progress-linear v-if="loading" color="primary" height="2" indeterminate/>
    <div v-if="visibleEvents.length" ref="feedViewport" class="event-scroll" :class="{ 'is-following': isFollowing }"
         tabindex="0" @scroll.passive="onScroll" @wheel.passive="onWheel" @touchstart.passive="onTouchStart"
         @touchmove.passive="onTouchMove" @pointerdown="onPointerDown" @keydown="onKeydown">
      <div ref="feedContent" class="event-list">
        <v-virtual-scroll ref="feed" :items="visibleEvents" :item-height="78" item-key="sequence" renderless>
          <template #default="{ item, itemRef }">
            <div :ref="itemRef" class="event-row" :class="`event-${item.kind.toLowerCase()}`">
              <EventMessageRow :session-id="sessionId" :event="item" @seek="$emit('seek', $event)"/>
            </div>
          </template>
        </v-virtual-scroll>
      </div>
    </div>
    <div v-else-if="!loading && !error" class="event-empty">
      <v-icon icon="mdi-comment-text-outline" size="28"/>
      <span v-if="!enabled">{{ t('components.events.notRecorded') }}</span>
      <span v-else-if="isSearching">{{ t('components.player.noSearchResults') }}</span>
      <span v-else>{{ t('components.events.empty') }}</span>
    </div>
  </v-card>
</template>

<script setup lang="ts">
import {computed, onBeforeUnmount, ref, shallowRef, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import {useSessionEventSnapshot} from '@/composables/useSessionEventSnapshot'
import {usePlaybackEvents} from '@/composables/usePlaybackEvents'
import {usePlaybackListSearch} from '@/composables/usePlaybackListSearch'
import type {EventKind, RecordedEvent} from '@/models/media'
import {DEFAULT_PLAYBACK_EVENT_RETENTION_COUNT} from '@/models/playbackPreferences'
import EventMessageRow from '@/components/events/EventMessageRow.vue'
import CardHeader from '@/components/common/CardHeader.vue'
import PlaybackListSearch from './PlaybackListSearch.vue'

const {t} = useI18n()

const props = withDefaults(defineProps<{
  sessionId: string;
  offsetMs: number;
  enabled: boolean;
  isLive: boolean;
  navigationRevision?: number;
  active?: boolean;
  retentionCount?: number
}>(), {active: true, retentionCount: DEFAULT_PLAYBACK_EVENT_RETENTION_COUNT})
defineEmits<{ seek: [offsetMs: number] }>()
const kind = ref<EventKind | 'all'>('all')
const {searchOpen, searchText, isSearching, matchesSearch, closeSearch} = usePlaybackListSearch(() => props.sessionId)
const {events, error, loading, refresh, resetRevision} = useSessionEventSnapshot({
  sessionId: () => props.sessionId,
  active: () => props.active,
  enabled: () => props.enabled,
  isLive: () => props.isLive
})
const follow = ref(true)
const isFollowing = computed(() => follow.value && !searchOpen.value)
const feed = ref<{ scrollToIndex: (index: number, position: 'end') => void }>()
const feedViewport = ref<HTMLElement>()
const feedContent = ref<HTMLElement>()
const pausedEvents = shallowRef<readonly RecordedEvent[]>()
const pausedOffset = ref(0)
const kinds = computed<{ title: string; value: EventKind }[]>(() => [
  {title: t('components.events.danmu'), value: 'Danmu'},
  {title: t('components.events.superChat'), value: 'SuperChat'},
  {title: t('components.events.gift'), value: 'Gift'},
  {title: t('components.events.guard'), value: 'Guard'},
  {title: t('components.events.redPocket'), value: 'RedPocket'},
])
const playbackEvents = usePlaybackEvents(
  () => isSearching.value ? events.value : pausedEvents.value ?? events.value,
  () => {
    if (isSearching.value) {
      return Infinity
    }
    return pausedEvents.value ? pausedOffset.value : props.offsetMs
  },
  kind,
  () => isSearching.value ? Infinity : props.retentionCount,
)
const visibleEvents = computed(() => isSearching.value ? playbackEvents.value.filter(item => matchesSearch(item.text, item.userName)) : playbackEvents.value)
let touchY: number | undefined
let resumeOnScroll = false
let followFrame = 0

watch(() => props.navigationRevision ?? 0, () => {
  pausedEvents.value = undefined
  follow.value = true
})
watch(follow, enabled => {
  resumeOnScroll = false
  if (followFrame) {
    cancelAnimationFrame(followFrame)
    followFrame = 0
  }
  if (enabled) {
    pausedEvents.value = undefined
  } else {
    pausedOffset.value = props.offsetMs
    pausedEvents.value = events.value
  }
}, {flush: 'sync'})
watch([() => props.sessionId, () => props.enabled, resetRevision], () => {
  pausedEvents.value = undefined
  follow.value = true
}, {flush: 'sync'})
watch([() => visibleEvents.value.length, () => visibleEvents.value.at(-1)?.sequence, isFollowing, () => props.active], () => {
  if (props.active && isFollowing.value && visibleEvents.value.length) {
    feed.value?.scrollToIndex(visibleEvents.value.length - 1, 'end')
    scheduleFollowToBottom()
  }
}, {flush: 'post'})
watch([searchText, kind], () => {
  if (props.active && searchOpen.value && feedViewport.value) {
    feedViewport.value.scrollTop = 0
  }
}, {flush: 'post'})
watch([feedViewport, feedContent], ([viewport, content], _previous, onCleanup) => {
  if (!viewport || !content) {
    return
  }
  const observer = new ResizeObserver(scheduleFollowToBottom)
  observer.observe(content)
  observer.observe(viewport)
  onCleanup(() => observer.disconnect())
}, {flush: 'post'})

function scheduleFollowToBottom(): void {
  if (!props.active || !isFollowing.value || !visibleEvents.value.length || followFrame) {
    return
  }
  followFrame = requestAnimationFrame(() => {
    followFrame = 0
    const viewport = feedViewport.value
    if (props.active && isFollowing.value && visibleEvents.value.length && viewport) {
      viewport.scrollTop = viewport.scrollHeight
    }
  })
}

function onScroll(event: Event): void {
  if (searchOpen.value) {
    return
  }
  const element = event.target as HTMLElement
  const distance = element.scrollHeight - element.scrollTop - element.clientHeight
  if (follow.value) {
    if (Math.abs(distance) > 1) {
      scheduleFollowToBottom()
    }
  } else if (resumeOnScroll && distance <= 1) {
    follow.value = true
  }
}

function onWheel(event: WheelEvent): void {
  if (searchOpen.value || event.ctrlKey) {
    return
  }
  if (event.deltaY < 0) {
    follow.value = false
    resumeOnScroll = false
  } else if (event.deltaY > 0) {
    resumeOnScroll = true
  }
}

function onTouchStart(event: TouchEvent): void {
  touchY = event.touches[0]?.clientY
}

function onTouchMove(event: TouchEvent): void {
  if (searchOpen.value) {
    return
  }
  const nextY = event.touches[0]?.clientY
  if (nextY !== undefined && touchY !== undefined && nextY > touchY) {
    follow.value = false
    resumeOnScroll = false
  } else if (nextY !== undefined && touchY !== undefined && nextY < touchY) {
    resumeOnScroll = true
  }
  touchY = nextY
}

function onPointerDown(event: PointerEvent): void {
  if (!searchOpen.value && event.target === event.currentTarget) {
    follow.value = false
    resumeOnScroll = true
  }
}

function onKeydown(event: KeyboardEvent): void {
  if (searchOpen.value) {
    return
  }
  if (['ArrowUp', 'PageUp', 'Home'].includes(event.key) || (event.key === ' ' && event.shiftKey)) {
    follow.value = false
    resumeOnScroll = false
  } else if (['ArrowDown', 'PageDown', 'End'].includes(event.key) || (event.key === ' ' && !event.shiftKey)) {
    resumeOnScroll = true
  }
}

watch([() => props.active, searchOpen], ([active, searching]) => {
  if ((!active || searching) && followFrame) {
    cancelAnimationFrame(followFrame)
    followFrame = 0
  }
})
onBeforeUnmount(() => {
  if (followFrame) {
    cancelAnimationFrame(followFrame)
  }
})
</script>

<style scoped>
.event-panel {
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
}

.feed-heading, .feed-tabs {
  flex-shrink: 0;
}

.feed-heading {
  flex-wrap: nowrap;
  gap: 8px;
}

.feed-heading :deep(.card-header-title) {
  flex-basis: 0;
}

.feed-heading :deep(.card-header-actions) {
  flex-shrink: 0;
  gap: 4px;
}

.feed-tabs :deep(.v-slide-group__content) {
  gap: 4px;
}

.feed-tabs :deep(.v-chip) {
  font-size: .7rem;
  margin-inline: 0;
}

.event-scroll {
  flex: 1;
  min-height: 0;
  position: relative;
  overflow-y: auto;
  overscroll-behavior-y: contain;
}

.event-scroll.is-following {
  overflow-anchor: none;
}

.event-list {
  min-height: 100%;
  display: flex;
  flex-direction: column;
  justify-content: flex-start;
}

.event-row, .event-list :deep(.v-virtual-scroll__spacer) {
  flex-shrink: 0;
}

.event-row {
  padding: 13px 16px;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), .045);
}

.event-empty {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 15px;
  align-items: center;
  justify-content: center;
  font-size: .8rem;
  color: rgba(var(--v-theme-on-surface), .3);
}
</style>
