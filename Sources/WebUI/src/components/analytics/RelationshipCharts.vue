<template>
  <div class="relationship-charts">
    <CardHeader embedded class="mb-4" :title="t('replay.title')" />
    <form class="range-controls" @submit.prevent="applyRange">
      <label>{{ t('replay.start') }}<input v-model.number="startSeconds" type="number" min="0" :max="durationMs / 1000" step="any" required /></label>
      <label>{{ t('replay.end') }}<input v-model.number="endSeconds" type="number" min="0" :max="durationMs / 1000" step="any" required /></label>
      <label class="metric-select">{{ t('views.common.superChat') }}<select v-model="scMetric"><option value="superChatCount">{{ t('replay.scCount') }}</option><option value="superChatAmount">{{ t('replay.scAmount') }}</option></select></label>
      <div class="range-actions">
        <v-btn type="submit" size="small" variant="tonal">{{ t('replay.apply') }}</v-btn>
        <v-btn size="small" variant="text" @click="emit('range', undefined)">{{ t('replay.reset') }}</v-btn>
      </div>
    </form>
    <p v-if="invalidRange" role="alert" class="text-error text-caption mt-2">{{ t('replay.invalidRange') }}</p>
    <p class="text-caption muted mt-4">{{ rangeLabel }}</p>
    <EChartSurface :option="timelineOption" :label="t('replay.timeline')" @zoom="selectRange" @point="selectPoint" />
    <div class="point-detail">
      <span class="mono">{{ t('replay.selectedPoint', { time: formatDuration(selectedPoint) }) }}</span>
      <span class="mono muted">{{ t('replay.totalDuration', { duration: formatDuration(durationMs) }) }}</span>
    </div>
    <div class="scatter-heading">
      <div class="d-flex align-center ga-2">
        <h3 class="text-subtitle-1">{{ t('replay.scatter') }}</h3>
        <v-tooltip :text="t('replay.scatterHelp')" location="top"><template #activator="{ props: tooltipProps }"><span v-bind="tooltipProps" class="text-caption muted" tabindex="0">{{ t('replay.scatterCount', { count: points.length }) }}</span></template></v-tooltip>
      </div>
      <label class="metric-select">{{ t('replay.comparison') }}<select v-model="scatterMetric"><option value="danmuCount">{{ t('replay.danmu') }}</option><option value="superChatCount">{{ t('replay.scCount') }}</option><option value="superChatAmount">{{ t('replay.scAmount') }}</option></select></label>
    </div>
    <EChartSurface :option="scatterOption" :label="t('replay.scatter')" :height="300" @point="selectPoint" />
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import type { EChartsCoreOption } from 'echarts/core'
import type { Analytics } from '@/models/media'
import type { AnalyticsRange } from '@/models/analyticsRange'
import { formatDuration, formatMoney, formatNumber } from '@/utilities/format'
import CardHeader from '@/components/common/CardHeader.vue'
import EChartSurface from './EChartSurface.vue'
import { clampAnalyticsRange, onlineLineData, relationshipPoints, type RelationshipMetric } from './relationshipData'

const props = defineProps<{ report: Analytics; durationMs: number; sessionId: string; range?: AnalyticsRange; topics?: { title: string; startMs: number; endMs: number }[] }>()
const emit = defineEmits<{ range: [value: AnalyticsRange | undefined] }>()
const { t } = useI18n()
const scMetric = ref<'superChatCount' | 'superChatAmount'>('superChatCount')
const scatterMetric = ref<RelationshipMetric>('danmuCount')
const startSeconds = ref(0)
const endSeconds = ref(0)
const invalidRange = ref(false)
const selectedPoint = ref(0)
const effectiveRange = computed(() => props.range ?? { fromMs: 0, toMs: props.durationMs })
const rangeLabel = computed(() => props.range ? t('replay.selection', { from: formatDuration(props.range.fromMs), to: formatDuration(props.range.toMs) }) : t('replay.fullSession'))
const metricLabel = (metric: RelationshipMetric): string => t(metric === 'danmuCount' ? 'replay.danmu' : metric === 'superChatCount' ? 'replay.scCount' : 'replay.scAmount')
const points = computed(() => relationshipPoints(props.report.buckets, scatterMetric.value, effectiveRange.value))
const chartBounds = { left: 70, right: 48, outerBoundsMode: 'none' }
const axis = (gridIndex: number) => ({
  type: 'value', gridIndex, min: 0, max: Math.max(1, props.durationMs), boundaryGap: [0, 0], containShape: false,
  axisLabel: { formatter: (value: number) => formatDuration(value), color: '#8a98a7', hideOverlap: true },
  axisPointer: { label: { formatter: ({ value }: { value: number }) => formatDuration(value) } },
  splitLine: { show: false },
})
const timelineOption = computed<EChartsCoreOption>(() => ({
  animation: false,
  grid: [{ ...chartBounds, top: 35, height: 115 }, { ...chartBounds, top: 200, height: 105 }, { ...chartBounds, top: 355, height: 105 }],
  tooltip: { trigger: 'axis', renderMode: 'richText', axisPointer: { type: 'line' }, valueFormatter: (value: unknown) => value == null ? t('replay.noData') : formatNumber(Number(value), { maximumFractionDigits: 2 }) },
  axisPointer: { link: [{ xAxisIndex: [0, 1, 2] }] },
  xAxis: [axis(0), axis(1), axis(2)],
  yAxis: [t('replay.online'), t('replay.danmu'), metricLabel(scMetric.value)].map((name, gridIndex) => ({ type: 'value', gridIndex, name, min: 0, nameTextStyle: { color: '#8a98a7', align: 'left' }, axisLabel: { color: '#8a98a7' }, splitLine: { lineStyle: { color: '#8090a020' } } })),
  dataZoom: [{ type: 'slider', xAxisIndex: [0, 1, 2], left: chartBounds.left, right: chartBounds.right, bottom: 5, height: 24, startValue: effectiveRange.value.fromMs, endValue: effectiveRange.value.toMs, filterMode: 'none', labelFormatter: (value: number) => formatDuration(value) }, { type: 'inside', xAxisIndex: [0, 1, 2], filterMode: 'none', zoomOnMouseWheel: 'ctrl' }],
  series: [
    { name: t('replay.online'), type: 'line', xAxisIndex: 0, yAxisIndex: 0, step: 'end', showSymbol: false, connectNulls: false, data: onlineLineData(props.report.online?.intervals ?? []), lineStyle: { color: '#54b9b0', width: 2 }, itemStyle: { color: '#54b9b0' },
      markLine: { silent: true, symbol: 'none', data: [{ xAxis: selectedPoint.value }], lineStyle: { color: '#9e96dd', type: 'dashed' }, label: { show: false } },
      markArea: { silent: true, itemStyle: { color: '#a092df12' }, label: { show: false }, data: (props.topics ?? []).map(topic => [{ name: topic.title, xAxis: topic.startMs }, { xAxis: topic.endMs }]) } },
    { name: t('replay.danmu'), type: 'bar', xAxisIndex: 1, yAxisIndex: 1, data: props.report.buckets.map(bucket => [bucket.offsetMs, bucket.danmuCount]), itemStyle: { color: '#9e96dd' }, barMaxWidth: 12 },
    { name: metricLabel(scMetric.value), type: 'bar', xAxisIndex: 2, yAxisIndex: 2, data: props.report.buckets.map(bucket => [bucket.offsetMs, bucket[scMetric.value] ?? 0]), itemStyle: { color: '#d7a36a' }, barMaxWidth: 12 },
  ],
}))
const scatterOption = computed<EChartsCoreOption>(() => ({
  animation: false,
  grid: { ...chartBounds, top: 35, bottom: 55 },
  xAxis: { type: 'value', name: t('replay.online'), nameLocation: 'middle', nameGap: 32, min: 0, axisLabel: { color: '#8a98a7' }, splitLine: { lineStyle: { color: '#8090a020' } } },
  yAxis: { type: 'value', name: metricLabel(scatterMetric.value), min: 0, nameTextStyle: { align: 'left' }, axisLabel: { color: '#8a98a7' }, splitLine: { lineStyle: { color: '#8090a020' } } },
  tooltip: { renderMode: 'richText', formatter: (params: { value: number[] }) => `${formatDuration(params.value[2] ?? 0)}\n${t('replay.online')}: ${formatNumber(params.value[0] ?? 0, { maximumFractionDigits: 1 })}\n${metricLabel(scatterMetric.value)}: ${scatterMetric.value === 'superChatAmount' ? formatMoney(params.value[1] ?? 0) : formatNumber(params.value[1] ?? 0)}` },
  series: [{ type: 'scatter', data: points.value.map(value => ({ value, itemStyle: { color: Math.floor(selectedPoint.value / 60000) === Math.floor(value[2]! / 60000) ? '#d7a36a' : '#54b9b0' } })), symbolSize: 9, itemStyle: { opacity: 0.75 } }],
}))

watch(() => [props.range?.fromMs, props.range?.toMs, props.durationMs], () =>
{
  startSeconds.value = effectiveRange.value.fromMs / 1000
  endSeconds.value = effectiveRange.value.toMs / 1000
  invalidRange.value = false
}, { immediate: true })
watch(() => props.sessionId, () => { selectedPoint.value = 0 })

function applyRange(): void
{
  const from = Number(startSeconds.value) * 1000
  const to = Number(endSeconds.value) * 1000
  invalidRange.value = !Number.isFinite(from) || !Number.isFinite(to) || from < 0 || to > props.durationMs || to <= from
  if (!invalidRange.value)
  {
    selectRange(from, to)
  }
}
function selectRange(fromMs: number, toMs: number): void
{
  const value = clampAnalyticsRange(fromMs, toMs, props.durationMs)
  if (value)
  {
    emit('range', value.fromMs <= 0 && value.toMs >= props.durationMs ? undefined : value)
  }
}
function selectPoint(offsetMs: number): void
{
  selectedPoint.value = Math.max(0, Math.min(offsetMs, props.durationMs))
}
</script>

<style scoped>
.range-controls { display: flex; flex-wrap: wrap; align-items: flex-end; gap: 12px; }
.range-controls label, .metric-select { display: grid; gap: 5px; font-size: .75rem; color: rgba(var(--v-theme-on-surface), .7); }
input, select { box-sizing: border-box; border: 1px solid rgba(var(--v-theme-on-surface), .2); border-radius: 6px; color: rgb(var(--v-theme-on-surface)); background: rgb(var(--v-theme-surface)); padding: 6px 10px; height: 34px; font: inherit; }
input { width: 112px; }
.range-controls :deep(.v-btn) { height: 34px; margin: 0; }
.range-actions { display: flex; align-items: center; gap: 8px; }
.point-detail, .scatter-heading { display: flex; gap: 12px 20px; flex-wrap: wrap; }
.point-detail { align-items: center; justify-content: space-between; }
.point-detail { font-size: .8rem; padding: 12px 0; }
.scatter-heading { margin-top: 24px; align-items: flex-end; justify-content: flex-start; }
.scatter-heading > div { min-height: 34px; flex-wrap: wrap; }
@media(max-width: 600px) { .range-controls { gap: 12px 8px; }.scatter-heading { gap: 12px; }.scatter-heading > div { flex-basis: 100%; } }
</style>
