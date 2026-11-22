<template>
  <div class="playback-timeline">
    <div v-if="timeline || bookmarks?.length || loading || error" class="timeline-legend">
      <template v-if="timeline">
        <button class="timeline-filter heat-filter" type="button" :aria-pressed="showHeat"
                @click="showHeat = !showHeat"><span/>{{ t('components.player.danmuHeat') }}</button>
        <button v-for="kind in markerKinds" :key="kind" class="timeline-filter" :data-kind="kind" type="button"
                :aria-pressed="enabledKinds.has(kind)" :style="{ '--marker-color': colors[kind] }"
                @click="toggleKind(kind)"><span/>{{ kindName(kind) }}</button>
      </template>
      <button v-if="bookmarks?.length" class="timeline-filter" type="button" data-kind="Bookmark"
              :aria-pressed="showBookmarks" :style="{ '--marker-color': BOOKMARK_COLOR }"
              @click="showBookmarks = !showBookmarks"><span/>{{ t('components.bookmarks.title') }}</button>
      <span v-if="loading" class="timeline-loading" :aria-label="t('components.player.timelineLoading')">···</span>
      <button v-if="error" type="button" class="timeline-retry" :title="error" @click="emit('retry')">{{
        t('components.common.retry') }}</button>
    </div>
    <div v-if="timeline || bookmarks?.length" ref="plot" class="timeline-plot">
      <svg v-if="timeline && showHeat" class="timeline-heat" viewBox="0 0 1000 28" preserveAspectRatio="none" role="img"
           :aria-label="t('components.player.danmuHeat')" @mouseleave="hoveredHeat = undefined">
        <rect v-for="column in heatColumns" :key="column.offsetMs" :x="column.left * 10"
              :width="Math.max(.2, column.width * 10 - .5)" :y="28 - column.strength * 26"
              :height="column.strength * 26"
              :aria-label="t('components.player.heatSummary', { time: formatDuration(column.offsetMs), count: formatNumber(column.count) })"
              @mouseenter="showHeatDetail(column)" @click.stop="emit('seek', column.offsetMs)"/>
      </svg>
      <button v-for="marker in markers" :key="marker.key" type="button" class="timeline-marker" :data-kind="marker.kind"
              :style="markerStyle(marker)" :aria-label="markerLabel(marker)" @mouseenter="showMarkerDetail(marker)"
              @mouseleave="hoveredMarker = undefined" @focus="showMarkerDetail(marker)"
              @blur="hoveredMarker = undefined" @click.stop="emit('seek', marker.offsetMs)"><span
        v-if="marker.count > 1" class="marker-stack"/></button>
      <button v-for="bookmark in visibleBookmarks" :key="bookmark.id" type="button" class="timeline-marker"
              data-kind="Bookmark" :style="bookmarkStyle(bookmark)" :aria-label="bookmarkLabel(bookmark)"
              @mouseenter="showBookmarkDetail(bookmark)" @mouseleave="hoveredBookmark = undefined"
              @focus="showBookmarkDetail(bookmark)" @blur="hoveredBookmark = undefined"
              @click.stop="emit('seek', bookmark.offsetMs)"/>
      <div v-if="hoveredMarker || hoveredHeat || hoveredBookmark" class="timeline-tooltip" role="tooltip"
           :style="{ left: `${tooltipLeft}px` }">
        <template v-if="hoveredBookmark">
          <div class="tooltip-heading"><strong>{{ t('components.bookmarks.title') }}</strong></div>
          <div class="tooltip-time mono">{{ formatTimecode(hoveredBookmark.offsetMs) }}</div>
          <div class="tooltip-text">{{ hoveredBookmark.title }}</div>
        </template>
        <template v-else-if="hoveredMarker">
          <div class="tooltip-heading"><strong>{{ kindName(hoveredMarker.kind) }}</strong><span>{{ t('components.player.eventCount', { count: formatNumber(hoveredMarker.count) }) }}</span>
          </div>
          <div class="tooltip-time mono">{{ formatDuration(hoveredMarker.offsetMs) }}<template v-if="hoveredMarker.endOffsetMs > hoveredMarker.offsetMs"> – {{
              formatDuration(hoveredMarker.endOffsetMs) }}</template>
          </div>
          <div class="tooltip-text"><b v-if="hoveredMarker.userName">{{ hoveredMarker.userName }} · </b>{{
            hoveredMarker.text }}</div>
          <div v-if="hoveredMarker.quantity > 1" class="tooltip-note">{{ t('components.player.itemQuantity', { count:
            formatNumber(hoveredMarker.quantity) }) }}</div>
          <div v-if="hoveredMarker.amount !== null" class="tooltip-note">{{ t('components.player.observedAmount', {
            amount: formatMoney(hoveredMarker.amount) }) }}</div>
        </template>
        <template v-else-if="hoveredHeat"><strong>{{ t('components.player.danmuHeat') }}</strong>
          <div class="tooltip-time mono">{{ formatDuration(hoveredHeat.offsetMs) }}</div>
          <div>{{ t('components.events.messageCount', { count: formatNumber(hoveredHeat.count) }) }}</div>
        </template>
      </div>
    </div>
    <div class="timeline-track">
      <div v-if="selectedRange" class="timeline-selected-range" aria-hidden="true"><span
        :style="{ left: `${selectedRange[0] / Math.max(1, durationMs) * 100}%`, width: `${(selectedRange[1] - selectedRange[0]) / Math.max(1, durationMs) * 100}%` }"/>
      </div>
      <div class="timeline-gaps" aria-hidden="true"><span v-for="item in normalizedGaps" :key="item.startMs"
                                                          :style="{ left: `${item.startMs / durationMs * 100}%`, width: `${(item.endMs - item.startMs) / durationMs * 100}%` }"/>
      </div>
      <v-slider :model-value="modelValue" :max="Math.max(durationMs, 1)" :step="1" color="primary" track-color="white"
                track-opacity="0.17" thumb-size="12" track-size="4" hide-details density="compact"
                :aria-label="t('components.player.position')" @update:model-value="emit('update:modelValue', $event)"
                @start="emit('start')" @end="emit('end')"/>
    </div>
  </div>
</template>

<script setup lang="ts">
import {computed, ref, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import type {TimelineGap} from '@/models/media'
import type {SessionBookmark} from '@/models/sessionBookmark'
import type {ClipSelection} from '@/models/clipSelection'
import type {DanmuHeatBucket, SessionTimeline, TimelineMarkerKind} from '@/models/sessionTimeline'
import {formatDuration, formatMoney, formatNumber, formatTimecode} from '@/utilities/format'
import {clampOffset, normalizeTimelineGaps} from '@/utilities/timeline'
import {
  groupTimelineMarkers,
  timelineHeatColumns,
  TIMELINE_MARKER_KINDS,
  type DisplayTimelineMarker
} from '@/utilities/playbackTimeline'

const props = withDefaults(defineProps<{
  modelValue: number;
  durationMs: number;
  gaps: TimelineGap[];
  timeline?: SessionTimeline;
  bookmarks?: SessionBookmark[];
  selection?: ClipSelection;
  loading?: boolean;
  error?: string
}>(), {loading: false, error: ''})
const emit = defineEmits<{
  'update:modelValue': [value: number];
  start: [];
  end: [];
  seek: [offsetMs: number];
  retry: []
}>()
const {t} = useI18n()
const markerKinds = TIMELINE_MARKER_KINDS
const MARKER_LANE_HEIGHT = 8
const BOOKMARK_COLOR = '#f5c974'
const colors: Record<TimelineMarkerKind, string> = {
  Gift: '#f39473',
  SuperChat: '#7ebcf1',
  RedPocket: '#f07c9d',
  Guard: '#b5a0f2'
}
const enabledKinds = ref(new Set(markerKinds))
const showHeat = ref(true)
const showBookmarks = ref(true)
const plot = ref<HTMLElement>()
const plotWidth = ref(600)
const hoveredMarker = ref<DisplayTimelineMarker>()
const hoveredHeat = ref<DanmuHeatBucket>()
const hoveredBookmark = ref<SessionBookmark>()
const normalizedGaps = computed(() => normalizeTimelineGaps(props.gaps, props.durationMs))
const heatColumns = computed(() => timelineHeatColumns(props.timeline?.heat ?? [], props.timeline?.bucketWidthMs ?? 0, props.durationMs))
const markers = computed(() => groupTimelineMarkers((props.timeline?.markers ?? []).filter(item => enabledKinds.value.has(item.kind)), props.durationMs, Math.max(1, Math.floor(plotWidth.value / 14))))
const visibleBookmarks = computed(() => showBookmarks.value ? props.bookmarks ?? [] : [])
const hoverOffset = computed(() => hoveredBookmark.value?.offsetMs ?? hoveredMarker.value?.offsetMs ?? hoveredHeat.value?.offsetMs ?? 0)
const tooltipLeft = computed(() => Math.max(0, Math.min(props.durationMs > 0 ? hoverOffset.value / props.durationMs * plotWidth.value - 130 : 0, Math.max(0, plotWidth.value - 260))))
const selectedRange = computed(() => {
  const selection = props.selection
  if (selection?.startMs === undefined || selection.endMs === undefined || selection.endMs <= selection.startMs) {
    return undefined
  }
  return [clampOffset(selection.startMs, props.durationMs), clampOffset(selection.endMs, props.durationMs)]
})

function bookmarkLabel(bookmark: SessionBookmark): string {
  return t('components.bookmarks.jump', {title: bookmark.title, time: formatTimecode(bookmark.offsetMs)})
}

function bookmarkStyle(bookmark: SessionBookmark) {
  return {
    left: `${clampOffset(bookmark.offsetMs, props.durationMs) / Math.max(1, props.durationMs) * 100}%`,
    bottom: `${markerKinds.length * MARKER_LANE_HEIGHT}px`,
    '--marker-color': BOOKMARK_COLOR
  }
}

watch(plot, (element, _previous, onCleanup) => {
  if (!element) {
    return
  }
  const resize = new ResizeObserver(entries => {
    const width = entries[0]?.contentRect.width
    if (width && width > 0) {
      plotWidth.value = width
    }
  })
  resize.observe(element)
  const width = element.getBoundingClientRect().width
  if (width > 0) {
    plotWidth.value = width
  }
  onCleanup(() => resize.disconnect())
}, {flush: 'post'})
watch(markers, () => {
  hoveredMarker.value = undefined
})
watch(heatColumns, () => {
  hoveredHeat.value = undefined
})
watch(showHeat, () => {
  hoveredHeat.value = undefined
})
watch(visibleBookmarks, () => {
  hoveredBookmark.value = undefined
})

function kindName(kind: TimelineMarkerKind): string {
  const key = {Gift: 'gift', SuperChat: 'superChat', RedPocket: 'redPocket', Guard: 'membership'} as const
  return t(`components.events.${key[kind]}`)
}

function toggleKind(kind: TimelineMarkerKind): void {
  const selected = new Set(enabledKinds.value)
  if (selected.has(kind)) {
    selected.delete(kind)
  } else {
    selected.add(kind)
  }
  enabledKinds.value = selected
}

function markerStyle(marker: DisplayTimelineMarker) {
  return {
    left: `${marker.offsetMs / props.durationMs * 100}%`,
    bottom: `${markerKinds.indexOf(marker.kind) * MARKER_LANE_HEIGHT}px`,
    '--marker-color': colors[marker.kind]
  }
}

function markerLabel(marker: DisplayTimelineMarker): string {
  return t('components.player.markerSummary', {
    kind: kindName(marker.kind),
    time: formatDuration(marker.offsetMs),
    count: formatNumber(marker.count)
  })
}

function showMarkerDetail(marker: DisplayTimelineMarker): void {
  hoveredBookmark.value = undefined
  hoveredHeat.value = undefined
  hoveredMarker.value = marker
}

function showHeatDetail(bucket: DanmuHeatBucket): void {
  hoveredBookmark.value = undefined
  hoveredMarker.value = undefined
  hoveredHeat.value = bucket
}

function showBookmarkDetail(bookmark: SessionBookmark): void {
  hoveredMarker.value = undefined
  hoveredHeat.value = undefined
  hoveredBookmark.value = bookmark
}
</script>

<style scoped>
.playback-timeline {
  min-width: 0;
}

.timeline-selected-range {
  position: absolute;
  left: 8px;
  right: 8px;
  top: 50%;
  height: 12px;
  transform: translateY(-50%);
  pointer-events: none;
}

.timeline-selected-range span {
  position: absolute;
  height: 12px;
  border-radius: 3px;
  background: #f3947350;
}

.timeline-legend {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 5px 11px;
  min-height: 20px;
  margin: 2px 8px 3px;
}

.timeline-filter, .timeline-retry {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 0;
  border: 0;
  background: none;
  color: #bec4cf;
  font-size: .63rem;
  line-height: 1.4;
  cursor: pointer;
}

.timeline-filter > span {
  width: 5px;
  height: 5px;
  border-radius: 50%;
  background: var(--marker-color);
}

.heat-filter > span {
  background: #7ac9b8;
}

.timeline-filter[aria-pressed="false"] {
  opacity: .35;
}

.timeline-filter:hover {
  color: #fff;
}

.timeline-loading {
  margin-left: auto;
  color: #a8adb8;
  font-size: .7rem;
}

.timeline-retry {
  color: #f39473;
  margin-left: auto;
}

.timeline-plot {
  position: relative;
  height: 48px;
  margin: 0 8px -3px;
}

.timeline-heat {
  position: absolute;
  inset: 0;
  height: 44px;
  width: 100%;
  fill: #7ac9b8;
  opacity: .4;
  overflow: visible;
}

.timeline-heat rect {
  cursor: pointer;
}

.timeline-heat rect:hover {
  opacity: .6;
}

.timeline-marker {
  position: absolute;
  transform: translateX(-50%);
  width: 10px;
  height: 7px;
  padding: 0;
  border: 1px solid #0d1017;
  border-radius: 2px;
  background: var(--marker-color);
  cursor: pointer;
  z-index: 1;
}

.timeline-marker:hover, .timeline-marker:focus-visible {
  outline: 1px solid #fff;
  z-index: 2;
}

.marker-stack {
  position: absolute;
  left: 1px;
  right: 1px;
  top: 1px;
  height: 1px;
  background: #16192399;
}

.timeline-tooltip {
  position: absolute;
  bottom: calc(100% + 7px);
  width: 260px;
  max-width: 100%;
  padding: 12px 14px;
  border: 1px solid #ffffff20;
  border-radius: 10px;
  background: #202630f5;
  box-shadow: 0 6px 24px #0006;
  color: #edf0f5;
  font-size: .74rem;
  line-height: 1.5;
  pointer-events: none;
  z-index: 5;
}

.tooltip-heading {
  display: flex;
  justify-content: space-between;
  gap: 8px;
}

.tooltip-heading span, .tooltip-note {
  color: #adb5c4;
  font-size: .66rem;
}

.tooltip-time {
  color: #8996aa;
  font-size: .65rem;
  margin: 4px 0;
}

.tooltip-text {
  overflow-wrap: anywhere;
  max-height: 4.5em;
  overflow: hidden;
}

.timeline-track {
  position: relative;
}

.timeline-gaps {
  position: absolute;
  left: 8px;
  right: 8px;
  top: 50%;
  height: 4px;
  transform: translateY(-50%);
  pointer-events: none;
  z-index: 1;
}

.timeline-gaps span {
  position: absolute;
  height: 4px;
  background: #e2b76b;
  opacity: .55;
}
</style>
