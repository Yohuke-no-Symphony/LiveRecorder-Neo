<template>
  <div class="activity-chart">
    <div class="chart-header mb-4">
      <div><h2 class="section-heading mb-1">{{ t('views.analytics.activityTitle') }}</h2><p class="text-caption muted">{{ t('views.analytics.perMinute') }}</p></div>
      <div class="chart-metrics" role="group" :aria-label="t('views.analytics.activityTitle')">
        <v-btn v-for="option in metricOptions" :key="option.value" :data-metric="option.value" :aria-pressed="metric === option.value" :variant="metric === option.value ? 'tonal' : 'text'" :color="metric === option.value ? 'primary' : undefined" size="small" @click="metric = option.value">{{ option.title }}</v-btn>
      </div>
    </div>
    <div class="chart-legend mb-7"><span v-for="item in series" :key="item.field"><i :style="{ background: `rgb(var(--v-theme-${item.color}))` }" />{{ item.label }}</span></div>
    <div v-if="!groups.length" class="empty-state py-10">{{ t('components.chart.empty') }}</div>
    <template v-else>
      <div class="chart-content">
        <span class="chart-max mono">{{ formatValue(maximum) }}</span>
        <svg viewBox="0 0 1000 220" role="group" :aria-label="selectedTitle" preserveAspectRatio="none" @click="clearSelection">
          <line v-for="y in [20, 80, 140, 200]" :key="y" x1="0" :y1="y" x2="1000" :y2="y" class="chart-grid" />
          <polyline v-if="metric === 'cumulative'" :points="cumulativePoints" class="chart-line" />
          <line v-if="activeGroup" :x1="x(activeGroup.offsetMs)" :x2="x(activeGroup.offsetMs)" y1="10" y2="205" class="chart-crosshair" />
          <g v-for="(group, index) in groups" :key="group.offsetMs" class="chart-bucket" :class="{ 'is-active': activeGroup?.offsetMs === group.offsetMs }" tabindex="0" role="button" :aria-label="describe(group)" :aria-pressed="selectedOffset === group.offsetMs" :aria-describedby="activeGroup?.offsetMs === group.offsetMs ? tooltipId : undefined" @mouseenter="previewOffset = group.offsetMs" @mouseleave="clearPreview(group.offsetMs)" @focus="previewOffset = group.offsetMs" @blur="clearPreview(group.offsetMs)" @click.stop="selectGroup(group.offsetMs)" @keydown.enter.prevent="selectGroup(group.offsetMs)" @keydown.space.prevent="selectGroup(group.offsetMs)" @keydown.escape.prevent.stop="clearSelection">
            <rect :x="x(hitStart(index))" y="10" :width="hitWidth(index)" height="195" fill="transparent" />
            <circle v-if="metric === 'cumulative'" :cx="x(group.offsetMs)" :cy="200 - group.cumulative / scaleMaximum * 180" :r="activeGroup?.offsetMs === group.offsetMs ? 6 : 4" class="chart-point" />
            <rect v-for="(value, seriesIndex) in group.values" v-else :key="series[seriesIndex]!.field" class="chart-bar" :data-series="series[seriesIndex]!.field" :x="barX(group.offsetMs, index, seriesIndex)" :y="barY(group.values, seriesIndex)" :width="metric === 'users' ? barWidth(index) / series.length : barWidth(index)" :height="value / scaleMaximum * 180" rx="1" :fill="`rgb(var(--v-theme-${series[seriesIndex]!.color}))`" />
          </g>
        </svg>
        <div v-if="activeGroup" :id="tooltipId" class="chart-tooltip" role="tooltip" :style="tooltipStyle">
          <div class="chart-tooltip-time mono">{{ formatDuration(activeGroup.offsetMs) }}</div>
          <dl>
            <div v-for="row in tooltipRows" :key="row.field" class="chart-tooltip-row">
              <dt><i :style="{ background: 'rgb(var(--v-theme-' + row.color + '))' }" />{{ row.label }}</dt>
              <dd class="chart-tooltip-value mono">{{ row.value }}</dd>
            </div>
          </dl>
        </div>
      </div>
      <div class="d-flex justify-space-between mt-3 mono text-caption muted"><span>00:00</span><span>{{ formatDuration(chartDurationMs / 2) }}</span><span>{{ formatDuration(chartDurationMs) }}</span></div>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, useId, watch, type CSSProperties } from 'vue'
import { useI18n } from 'vue-i18n'
import type { Analytics } from '@/models/media'
import { formatDuration, formatMoney, formatNumber } from '@/utilities/format'

type ChartMetric = 'danmu' | 'users' | 'revenue' | 'cumulative'
type BucketField = 'danmuCount' | 'activeUsers' | 'danmuUsers' | 'superChatAmount' | 'giftAmount' | 'guardAmount' | 'paidAmount'
interface ChartSeries
{
  field: BucketField
  label: string
  color: string
}
interface ChartGroup
{
  offsetMs: number
  values: number[]
  cumulative: number
}

const BUCKET_WIDTH_MS = 60000
const { t } = useI18n()
const props = defineProps<{ buckets: Analytics['buckets']; durationMs: number }>()
const metric = ref<ChartMetric>('danmu')
const tooltipId = useId()
const previewOffset = ref<number>()
const selectedOffset = ref<number>()
const metricOptions = computed(() => [
  { value: 'danmu' as const, title: t('views.analytics.chartDanmu') },
  { value: 'users' as const, title: t('views.analytics.chartUsers') },
  { value: 'revenue' as const, title: t('views.analytics.chartRevenue') },
  { value: 'cumulative' as const, title: t('views.analytics.chartCumulative') },
])
const selectedTitle = computed(() => metricOptions.value.find(option => option.value === metric.value)!.title)
const monetary = computed(() => metric.value === 'revenue' || metric.value === 'cumulative')
const series = computed<ChartSeries[]>(() =>
{
  if (metric.value === 'revenue')
  {
    return [
      { field: 'superChatAmount', label: t('views.common.superChat'), color: 'info' },
      { field: 'giftAmount', label: t('views.common.gifts'), color: 'primary' },
      { field: 'guardAmount', label: t('views.common.guard'), color: 'warning' },
    ]
  }
  if (metric.value === 'users')
  {
    return [{ field: 'activeUsers', label: t('views.analytics.interactionUsers'), color: 'secondary' }, { field: 'danmuUsers', label: t('views.analytics.danmuUsers'), color: 'info' }]
  }
  return metric.value === 'cumulative'
    ? [{ field: 'paidAmount', label: t('views.analytics.chartCumulative'), color: 'primary' }]
    : [{ field: 'danmuCount', label: t('views.common.danmu'), color: 'secondary' }]
})
const orderedBuckets = computed(() => props.buckets.filter(bucket => Number.isFinite(bucket.offsetMs) && bucket.offsetMs >= 0).sort((first, second) => first.offsetMs - second.offsetMs))
const chartDurationMs = computed(() => Math.max(Number.isFinite(props.durationMs) ? props.durationMs : 0, (orderedBuckets.value.at(-1)?.offsetMs ?? 0) + BUCKET_WIDTH_MS))
const groups = computed<ChartGroup[]>(() =>
{
  let cumulative = 0
  return orderedBuckets.value.map(bucket =>
  {
    cumulative += bucket.paidAmount
    return { offsetMs: bucket.offsetMs, values: series.value.map(item => bucket[item.field]), cumulative }
  })
})
const maximum = computed(() => groups.value.reduce((maximum, group) => Math.max(maximum, metric.value === 'cumulative' ? group.cumulative : metric.value === 'revenue' ? group.values.reduce((sum, value) => sum + value, 0) : Math.max(...group.values)), 0))
const scaleMaximum = computed(() => maximum.value > 0 ? maximum.value : 1)
const cumulativePoints = computed(() => ['0,200', ...groups.value.map(group => `${x(group.offsetMs)},${200 - group.cumulative / scaleMaximum.value * 180}`), `1000,${200 - (groups.value.at(-1)?.cumulative ?? 0) / scaleMaximum.value * 180}`].join(' '))

const activeGroup = computed(() => groups.value.find(group => group.offsetMs === (previewOffset.value ?? selectedOffset.value)))
const tooltipRows = computed(() =>
{
  const group = activeGroup.value
  if (!group)
  {
    return []
  }
  return series.value.map((item, index) => ({
    field: item.field,
    label: item.label,
    color: item.color,
    value: formatValue(metric.value === 'cumulative' ? group.cumulative : group.values[index]!),
  }))
})
const tooltipStyle = computed<CSSProperties>(() =>
{
  const group = activeGroup.value
  if (!group)
  {
    return {}
  }
  const value = metric.value === 'cumulative' ? group.cumulative
    : metric.value === 'revenue' ? group.values.reduce((sum, value) => sum + value, 0) : Math.max(...group.values)
  return { '--tooltip-anchor-x': String(x(group.offsetMs) / 10) + '%', top: String(Math.max(12, Math.min(96, 180 - value / scaleMaximum.value * 180))) + 'px' }
})
watch(() => props.buckets, clearSelection)

function clearPreview(offsetMs: number): void
{
  if (previewOffset.value === offsetMs)
  {
    previewOffset.value = undefined
  }
}
function clearSelection(): void
{
  previewOffset.value = undefined
  selectedOffset.value = undefined
}
function selectGroup(offsetMs: number): void
{
  if (selectedOffset.value === offsetMs)
  {
    clearSelection()
    return
  }
  selectedOffset.value = offsetMs
  previewOffset.value = offsetMs
}
function formatValue(value: number): string
{
  return monetary.value ? formatMoney(value) : formatNumber(value)
}
function x(offsetMs: number): number
{
  return offsetMs / chartDurationMs.value * 1000
}
function barWidth(index: number): number
{
  const start = groups.value[index]!.offsetMs
  const end = Math.min(start + BUCKET_WIDTH_MS, groups.value[index + 1]?.offsetMs ?? chartDurationMs.value, chartDurationMs.value)
  return Math.max(1, Math.min(80, x(Math.max(0, end - start)) - 2))
}
function hitStart(index: number): number
{
  const current = groups.value[index]!.offsetMs
  const previous = groups.value[index - 1]?.offsetMs
  return metric.value === 'cumulative' && previous !== undefined ? (previous + current) / 2 : current
}
function hitWidth(index: number): number
{
  const current = groups.value[index]!.offsetMs
  const next = groups.value[index + 1]?.offsetMs
  const end = metric.value === 'cumulative' ? next !== undefined ? (current + next) / 2 : chartDurationMs.value
    : Math.min(current + BUCKET_WIDTH_MS, next ?? chartDurationMs.value, chartDurationMs.value)
  return Math.max(1, x(end - hitStart(index)))
}
function barX(offsetMs: number, index: number, seriesIndex: number): number
{
  return x(offsetMs) + (metric.value === 'users' ? seriesIndex * barWidth(index) / series.value.length : 0)
}
function barY(values: number[], index: number): number
{
  const height = metric.value === 'revenue' ? values.slice(0, index + 1).reduce((sum, value) => sum + value, 0) : values[index]!
  return 200 - height / scaleMaximum.value * 180
}
function describe(group: ChartGroup): string
{
  if (metric.value === 'cumulative')
  {
    return `${formatDuration(group.offsetMs)} · ${selectedTitle.value} ${formatMoney(group.cumulative)}`
  }
  return `${formatDuration(group.offsetMs)} · ${series.value.map((item, index) => `${item.label} ${formatValue(group.values[index]!)}`).join(' · ')}`
}
</script>

<style scoped>
.chart-header { display: flex; justify-content: space-between; align-items: center; gap: 16px; flex-wrap: wrap; }
.chart-metrics { display: flex; gap: 4px; flex-wrap: wrap; }
.chart-legend { display: flex; gap: 20px; flex-wrap: wrap; font-size: .75rem; color: rgba(var(--v-theme-on-surface), .65); }
.chart-legend > span { display: flex; align-items: center; gap: 7px; }
.chart-legend i { display: block; width: 8px; height: 8px; border-radius: 2px; }
.chart-content { position: relative; padding-top: 12px; }
.chart-content svg { display: block; width: 100%; height: 220px; overflow: visible; }
.chart-grid { stroke: rgba(var(--v-theme-on-surface), .07); stroke-width: 1; }
.chart-bar { opacity: .75; transition: opacity .15s; }
.chart-line { fill: none; stroke: rgb(var(--v-theme-primary)); stroke-width: 2.5; vector-effect: non-scaling-stroke; }
.chart-point { fill: rgb(var(--v-theme-primary)); }
.chart-bucket { cursor: crosshair; outline: none; }
.chart-bucket:hover .chart-bar, .chart-bucket:focus .chart-bar, .chart-bucket.is-active .chart-bar { opacity: 1; }
.chart-bucket:focus > rect:first-of-type { stroke: rgb(var(--v-theme-on-surface)); stroke-width: 1; }
.chart-max { position: absolute; top: -10px; left: 0; font-size: .65rem; color: rgba(var(--v-theme-on-surface), .5); }
.chart-crosshair { stroke: rgb(var(--v-theme-primary)); stroke-width: 1; stroke-dasharray: 4 4; opacity: .6; pointer-events: none; }
.chart-tooltip { --tooltip-width: min(240px, calc(100% - 16px)); position: absolute; z-index: 2; left: clamp(8px, calc(var(--tooltip-anchor-x) - 120px), calc(100% - var(--tooltip-width) - 8px)); width: var(--tooltip-width); padding: 12px 14px; border: 1px solid rgba(var(--v-theme-on-surface), .14); border-radius: 8px; background: rgb(var(--v-theme-surface)); color: rgb(var(--v-theme-on-surface)); box-shadow: 0 8px 24px #0003; pointer-events: none; font-size: .75rem; }
.chart-tooltip-time { margin-bottom: 10px; color: rgba(var(--v-theme-on-surface), .65); }
.chart-tooltip-row { display: flex; align-items: center; justify-content: space-between; gap: 16px; margin-top: 8px; }
.chart-tooltip-row dt { display: flex; align-items: center; gap: 7px; }
.chart-tooltip-row i { display: block; width: 7px; height: 7px; border-radius: 2px; flex-shrink: 0; }
.chart-tooltip-value { margin: 0; white-space: nowrap; }
</style>
