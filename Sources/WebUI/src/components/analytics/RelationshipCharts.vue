<template>
  <div class="relationship-charts">
    <CardHeader embedded class="mb-4" :title="t('replay.title')">
      <template #actions>
        <div class="metric-select"><span>{{ t('views.common.superChat') }}</span>
          <v-combobox :key="scMetricRevision" :model-value="scMetric" :items="scMetricOptions" :return-object="false"
                      :aria-label="t('views.common.superChat')" density="compact" variant="outlined" hide-details
                      class="metric-combobox" @update:model-value="setScMetric"/>
        </div>
      </template>
    </CardHeader>
    <EChartSurface :option="timelineOption" :label="t('replay.timeline')" :height="500" @zoom="selectRange"
                   @point="selectPoint"/>
    <div class="scatter-heading">
      <div class="d-flex align-center ga-2">
        <h3 class="text-subtitle-1">{{ t('replay.scatter') }}</h3>
        <v-tooltip :text="t('replay.scatterHelp')" location="top">
          <template #activator="{ props: tooltipProps }"><span v-bind="tooltipProps" class="text-caption muted"
                                                               tabindex="0">{{ t('replay.scatterCount', { count: points.length }) }}</span>
          </template>
        </v-tooltip>
      </div>
      <div class="metric-select"><span>{{ t('replay.comparison') }}</span>
        <v-combobox :key="scatterMetricRevision" :model-value="scatterMetric" :items="scatterMetricOptions"
                    :return-object="false" :aria-label="t('replay.comparison')" density="compact" variant="outlined"
                    hide-details class="metric-combobox" @update:model-value="setScatterMetric"/>
      </div>
    </div>
    <EChartSurface :option="scatterOption" :label="t('replay.scatter')" :height="300" @point="selectPoint"/>
  </div>
</template>

<script setup lang="ts">
import {computed, ref, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import type {EChartsCoreOption} from 'echarts/core'
import type {Analytics} from '@/models/media'
import type {AnalyticsRange} from '@/models/analyticsRange'
import {formatDuration, formatMoney, formatNumber} from '@/utilities/format'
import CardHeader from '@/components/common/CardHeader.vue'
import EChartSurface from './EChartSurface.vue'
import {clampAnalyticsRange, onlineLineData, relationshipPoints, type RelationshipMetric} from './relationshipData'

const props = defineProps<{
  report: Analytics;
  durationMs: number;
  sessionId: string;
  range?: AnalyticsRange;
  topics?: { title: string; startMs: number; endMs: number }[]
}>()
const emit = defineEmits<{ range: [value: AnalyticsRange | undefined] }>()
const {t} = useI18n()
const scMetric = ref<'superChatCount' | 'superChatAmount'>('superChatCount')
const scatterMetric = ref<RelationshipMetric>('danmuCount')
const scMetricRevision = ref(0)
const scatterMetricRevision = ref(0)
const selectedPoint = ref(0)
const effectiveRange = computed(() => props.range ?? {fromMs: 0, toMs: props.durationMs})
const metricLabel = (metric: RelationshipMetric): string => t(metric === 'danmuCount' ? 'replay.danmu' : metric === 'superChatCount' ? 'replay.scCount' : 'replay.scAmount')
const scMetricOptions = computed(() => ['superChatCount', 'superChatAmount'].map(value => ({
  value,
  title: metricLabel(value as RelationshipMetric)
})))
const scatterMetricOptions = computed(() => [{value: 'danmuCount', title: t('replay.danmu')}, ...scMetricOptions.value])
const points = computed(() => relationshipPoints(props.report.buckets, scatterMetric.value, effectiveRange.value))
const chartBounds = {left: 70, right: 48, outerBoundsMode: 'none'}
const axis = (gridIndex: number) => ({
  type: 'value', gridIndex, min: 0, max: Math.max(1, props.durationMs), boundaryGap: [0, 0], containShape: false,
  axisLabel: {formatter: (value: number) => formatDuration(value), color: '#8a98a7', hideOverlap: true},
  axisPointer: {label: {formatter: ({value}: { value: number }) => formatDuration(value)}},
  splitLine: {show: false},
})
const timelineOption = computed<EChartsCoreOption>(() => ({
  animation: false,
  grid: [{...chartBounds, top: 35, height: 115}, {...chartBounds, top: 200, height: 105}, {
    ...chartBounds,
    top: 355,
    height: 105
  }],
  tooltip: {
    trigger: 'axis',
    renderMode: 'richText',
    axisPointer: {type: 'line'},
    valueFormatter: (value: unknown) => value == null ? t('replay.noData') : formatNumber(Number(value), {maximumFractionDigits: 2})
  },
  axisPointer: {link: [{xAxisIndex: [0, 1, 2]}]},
  xAxis: [axis(0), axis(1), axis(2)],
  yAxis: [t('replay.online'), t('replay.danmu'), metricLabel(scMetric.value)].map((name, gridIndex) => ({
    type: 'value',
    gridIndex,
    name,
    min: 0,
    nameTextStyle: {color: '#8a98a7', align: 'left'},
    axisLabel: {color: '#8a98a7'},
    splitLine: {lineStyle: {color: '#8090a020'}}
  })),
  dataZoom: [{
    type: 'inside',
    xAxisIndex: [0, 1, 2],
    startValue: effectiveRange.value.fromMs,
    endValue: effectiveRange.value.toMs,
    filterMode: 'none',
    zoomOnMouseWheel: 'ctrl'
  }],
  series: [
    {
      name: t('replay.online'),
      type: 'line',
      xAxisIndex: 0,
      yAxisIndex: 0,
      step: 'end',
      showSymbol: false,
      connectNulls: false,
      data: onlineLineData(props.report.online?.intervals ?? []),
      lineStyle: {color: '#54b9b0', width: 2},
      itemStyle: {color: '#54b9b0'},
      markLine: {
        silent: true,
        symbol: 'none',
        data: [{xAxis: selectedPoint.value}],
        lineStyle: {color: '#9e96dd', type: 'dashed'},
        label: {show: false}
      },
      markArea: {
        silent: true,
        itemStyle: {color: '#a092df12'},
        label: {show: false},
        data: (props.topics ?? []).map(topic => [{name: topic.title, xAxis: topic.startMs}, {xAxis: topic.endMs}])
      }
    },
    {
      name: t('replay.danmu'),
      type: 'bar',
      xAxisIndex: 1,
      yAxisIndex: 1,
      data: props.report.buckets.map(bucket => [bucket.offsetMs, bucket.danmuCount]),
      itemStyle: {color: '#9e96dd'},
      barMaxWidth: 12
    },
    {
      name: metricLabel(scMetric.value),
      type: 'bar',
      xAxisIndex: 2,
      yAxisIndex: 2,
      data: props.report.buckets.map(bucket => [bucket.offsetMs, bucket[scMetric.value] ?? 0]),
      itemStyle: {color: '#d7a36a'},
      barMaxWidth: 12
    },
  ],
}))
const scatterOption = computed<EChartsCoreOption>(() => ({
  animation: false,
  grid: {...chartBounds, top: 35, bottom: 55},
  xAxis: {
    type: 'value',
    name: t('replay.online'),
    nameLocation: 'middle',
    nameGap: 32,
    min: 0,
    axisLabel: {color: '#8a98a7'},
    splitLine: {lineStyle: {color: '#8090a020'}}
  },
  yAxis: {
    type: 'value',
    name: metricLabel(scatterMetric.value),
    min: 0,
    nameTextStyle: {align: 'left'},
    axisLabel: {color: '#8a98a7'},
    splitLine: {lineStyle: {color: '#8090a020'}}
  },
  tooltip: {
    renderMode: 'richText',
    formatter: (params: {
      value: number[]
    }) => `${formatDuration(params.value[2] ?? 0)}\n${t('replay.online')}: ${formatNumber(params.value[0] ?? 0, {maximumFractionDigits: 1})}\n${metricLabel(scatterMetric.value)}: ${scatterMetric.value === 'superChatAmount' ? formatMoney(params.value[1] ?? 0) : formatNumber(params.value[1] ?? 0)}`
  },
  series: [{
    type: 'scatter',
    data: points.value.map(value => ({
      value,
      itemStyle: {color: Math.floor(selectedPoint.value / 60000) === Math.floor(value[2]! / 60000) ? '#d7a36a' : '#54b9b0'}
    })),
    symbolSize: 9,
    itemStyle: {opacity: 0.75}
  }],
}))

watch(() => props.sessionId, () => {
  selectedPoint.value = 0
})

function setScMetric(value: unknown): void {
  if (value === 'superChatCount' || value === 'superChatAmount') {
    scMetric.value = value
  } else {
    scMetricRevision.value++
  }
}

function setScatterMetric(value: unknown): void {
  if (value === 'danmuCount' || value === 'superChatCount' || value === 'superChatAmount') {
    scatterMetric.value = value
  } else {
    scatterMetricRevision.value++
  }
}

function selectRange(fromMs: number, toMs: number): void {
  const value = clampAnalyticsRange(fromMs, toMs, props.durationMs)
  if (value) {
    emit('range', value.fromMs <= 0 && value.toMs >= props.durationMs ? undefined : value)
  }
}

function selectPoint(offsetMs: number): void {
  selectedPoint.value = Math.max(0, Math.min(offsetMs, props.durationMs))
}
</script>

<style scoped>
.metric-select {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: .75rem;
  color: rgba(var(--v-theme-on-surface), .7);
  max-width: 100%;
}

.metric-select > span {
  flex-shrink: 0;
}

.metric-combobox {
  width: 210px;
  min-width: 0;
}

.metric-combobox :deep(input), .metric-combobox :deep(.v-field__input) {
  font-size: .8rem;
}

.scatter-heading {
  display: flex;
  gap: 12px 20px;
  flex-wrap: wrap;
  margin-top: 24px;
  align-items: center;
  justify-content: space-between;
}

.scatter-heading > div {
  min-height: 34px;
  flex-wrap: wrap;
}

@media (max-width: 600px) {
  .scatter-heading {
    gap: 12px;
  }

  .metric-combobox {
    width: 190px;
  }

  .scatter-heading > div:first-child {
    flex-basis: 100%;
  }
}
</style>
