<template>
  <div class="analytics-range-overview">
    <div class="overview-legend" :aria-label="t('contributions.categories')">
      <span v-for="kind in markerKinds" :key="kind" :style="{ '--marker-color': colors[kind] }"><i />{{ kindName(kind) }}</span>
    </div>
    <div ref="markerPlot" class="overview-marker-plot" :style="{ height: `${activeKinds.length ? activeKinds.length * 5 + 2 : 0}px` }" role="group" :aria-label="t('contributions.timeline')">
      <div class="overview-selection" :style="{ left: `${percentage(effectiveRange.fromMs)}%`, width: `${percentage(effectiveRange.toMs - effectiveRange.fromMs)}%` }" aria-hidden="true" />
      <v-tooltip v-for="marker in markers" :key="marker.key" :text="markerLabel(marker)" location="bottom" :open-on-click="false">
        <template #activator="{ props: tooltipProps }">
          <span v-bind="tooltipProps" class="overview-marker" :data-kind="marker.kind" :data-in-range="marker.endOffsetMs >= effectiveRange.fromMs && marker.offsetMs <= effectiveRange.toMs" :style="markerStyle(marker)" tabindex="0" role="img" :aria-label="markerLabel(marker)"><i v-if="marker.count > 1" /></span>
        </template>
      </v-tooltip>
    </div>
    <EChartSurface :option="option" :label="t('replay.rangeTitle')" :height="28" @zoom="selectRange" />
    <div class="overview-times mono muted"><span>{{ formatDuration(0, true) }}</span><span>{{ formatDuration(durationMs, true) }}</span></div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import type { EChartsCoreOption } from 'echarts/core'
import type { Analytics } from '@/models/media'
import type { AnalyticsRange } from '@/models/analyticsRange'
import type { SessionTimeline, TimelineMarkerKind } from '@/models/sessionTimeline'
import { formatDuration, formatMoney, formatNumber } from '@/utilities/format'
import { groupTimelineMarkers, TIMELINE_MARKER_KINDS, type DisplayTimelineMarker } from '@/utilities/playbackTimeline'
import { clampAnalyticsRange, onlineLineData } from './relationshipData'
import EChartSurface from './EChartSurface.vue'

const props = defineProps<{ range?: AnalyticsRange; durationMs: number; report: Analytics; timeline?: SessionTimeline }>()
const emit = defineEmits<{ range: [value: AnalyticsRange | undefined] }>()
const { t } = useI18n()
const markerKinds = TIMELINE_MARKER_KINDS
const colors: Record<TimelineMarkerKind, string> = { Gift: '#f39473', SuperChat: '#7ebcf1', RedPocket: '#f07c9d', Guard: '#b5a0f2' }
const markerPlot = ref<HTMLElement>()
const plotWidth = ref(600)
const effectiveRange = computed(() => props.range ?? { fromMs: 0, toMs: props.durationMs })
const markers = computed(() => groupTimelineMarkers(props.timeline?.markers ?? [], props.durationMs, Math.max(1, Math.floor(plotWidth.value / 12))))
const activeKinds = computed(() => markerKinds.filter(kind => markers.value.some(marker => marker.kind === kind)))
const option = computed<EChartsCoreOption>(() => ({
  animation: false,
  grid: { left: 6, right: 6, top: 0, height: 0 },
  xAxis: [{ type: 'value', min: 0, max: Math.max(1, props.durationMs), show: false }],
  yAxis: [{ type: 'value', show: false }],
  dataZoom: [{
    type: 'slider', xAxisIndex: [0], left: 6, right: 6, top: 2, height: 22,
    startValue: effectiveRange.value.fromMs, endValue: effectiveRange.value.toMs,
    filterMode: 'none', brushSelect: false, realtime: false, showDetail: false,
    showDataShadow: true, moveHandleSize: 0,
    backgroundColor: '#8090a014', borderColor: '#8090a04d', fillerColor: '#9e96dd38',
    dataBackground: { lineStyle: { color: '#8e9cb6', opacity: .6 }, areaStyle: { color: '#8e9cb6', opacity: .15 } },
    selectedDataBackground: { lineStyle: { color: '#9e96dd', opacity: .85 }, areaStyle: { color: '#9e96dd', opacity: .3 } },
    handleStyle: { color: '#9e96dd', borderColor: '#c8c2ee' },
  }],
  series: [{ type: 'line', silent: true, showSymbol: false, connectNulls: false, lineStyle: { opacity: 0 }, data: onlineLineData(props.report.online?.intervals ?? []) }],
}))

watch(markerPlot, (element, _previous, onCleanup) =>
{
  if (!element)
  {
    return
  }
  const resize = new ResizeObserver(entries =>
  {
    const width = entries[0]?.contentRect.width
    if (width && width > 0)
    {
      plotWidth.value = width
    }
  })
  resize.observe(element)
  const width = element.getBoundingClientRect().width
  if (width > 0)
  {
    plotWidth.value = width
  }
  onCleanup(() => resize.disconnect())
}, { flush: 'post' })

function percentage(offsetMs: number): number
{
  return Math.max(0, Math.min(100, offsetMs / Math.max(1, props.durationMs) * 100))
}

function kindName(kind: TimelineMarkerKind): string
{
  const keys = { Gift: 'gift', SuperChat: 'superChat', RedPocket: 'redPocket', Guard: 'membership' } as const
  return t(`components.events.${keys[kind]}`)
}

function markerStyle(marker: DisplayTimelineMarker)
{
  return { left: `${percentage(marker.offsetMs)}%`, top: `${activeKinds.value.indexOf(marker.kind) * 5 + 1}px`, '--marker-color': colors[marker.kind] }
}

function markerLabel(marker: DisplayTimelineMarker): string
{
  const details = [t('components.player.markerSummary', { kind: kindName(marker.kind), time: formatDuration(marker.offsetMs, true), count: formatNumber(marker.count) })]
  if (marker.userName || marker.text)
  {
    details.push([marker.userName, marker.text].filter(Boolean).join(' · '))
  }
  if (marker.quantity > 1)
  {
    details.push(t('components.player.itemQuantity', { count: formatNumber(marker.quantity) }))
  }
  if (marker.amount !== null)
  {
    details.push(t('components.player.observedAmount', { amount: formatMoney(marker.amount) }))
  }
  return details.join(' · ')
}

function selectRange(fromMs: number, toMs: number): void
{
  const range = clampAnalyticsRange(fromMs, toMs, props.durationMs)
  if (range)
  {
    emit('range', range.fromMs === 0 && range.toMs === props.durationMs ? undefined : range)
  }
}
</script>

<style scoped>
.analytics-range-overview { min-width: 0; width: 100%; }
.overview-legend { display: flex; flex-wrap: wrap; align-items: center; gap: 2px 12px; margin: 0 6px 2px; color: rgba(var(--v-theme-on-surface), .6); font-size: .62rem; line-height: 1.25; }
.overview-legend > span { display: inline-flex; align-items: center; gap: 5px; }
.overview-legend i { width: 6px; height: 6px; border-radius: 50%; background: var(--marker-color); }
.overview-marker-plot { position: relative; margin: 0 6px; }
.overview-selection { position: absolute; inset-block: 0; background: rgba(var(--v-theme-primary), .04); pointer-events: none; }
.overview-marker { position: absolute; transform: translateX(-50%); width: 9px; height: 5px; border: 1px solid rgb(var(--v-theme-background)); border-radius: 2px; background: var(--marker-color); cursor: default; }
.overview-marker[data-in-range="false"] { opacity: .35; }
.overview-marker:hover, .overview-marker:focus-visible { outline: 2px solid rgba(var(--v-theme-on-surface), .8); outline-offset: 1px; z-index: 1; }
.overview-marker > i { position: absolute; left: 1px; right: 1px; top: 1px; height: 1px; background: #16192399; }
.overview-times { display: flex; justify-content: space-between; gap: 12px; padding: 0 6px; font-size: .62rem; line-height: 1.2; }
</style>
