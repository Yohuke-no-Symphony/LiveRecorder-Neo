<template>
  <form class="analytics-range-toolbar" :aria-label="t('replay.rangeTitle')" :aria-busy="busy"
        @submit.prevent="applyRange">
    <div class="range-description"><span class="range-title"><v-icon icon="mdi-clock-outline" size="18"/>{{ t('replay.rangeTitle') }}<v-tooltip
      :text="pinLabel" location="bottom"><template #activator="{ props: tooltipProps }"><v-btn v-bind="tooltipProps"
                                                                                               class="range-pin"
                                                                                               type="button"
                                                                                               size="x-small"
                                                                                               variant="text"
                                                                                               :icon="pinned ? 'mdi-pin' : 'mdi-pin-off-outline'"
                                                                                               :color="pinned ? 'primary' : undefined"
                                                                                               :aria-label="pinLabel"
                                                                                               :aria-pressed="pinned"
                                                                                               :disabled="pinSaving"
                                                                                               @click="pinned = !pinned"/></template></v-tooltip></span><span
      class="text-caption muted">{{ rangeLabel }}</span></div>
    <div class="range-fields">
      <label>{{ t('replay.start') }}<input v-model="startInput" class="mono" type="text" placeholder="00:00:00"
                                           :title="t('replay.timeFormat')" :aria-invalid="invalidRange"
                                           :aria-describedby="invalidRange ? errorId : undefined" autocomplete="off"
                                           spellcheck="false" required/></label>
      <span class="range-separator" aria-hidden="true">—</span>
      <label>{{ t('replay.end') }}<input v-model="endInput" class="mono" type="text" placeholder="00:00:00"
                                         :title="t('replay.timeFormat')" :aria-invalid="invalidRange"
                                         :aria-describedby="invalidRange ? errorId : undefined" autocomplete="off"
                                         spellcheck="false" required/></label>
    </div>
    <div class="range-actions">
      <v-btn type="submit" size="small" variant="tonal" :disabled="durationMs <= 0">{{ t('replay.apply') }}</v-btn>
      <v-btn type="button" size="small" variant="text" :disabled="!range" @click="resetRange">{{ t('replay.reset') }}
      </v-btn>
    </div>
    <p v-if="invalidRange" :id="errorId" role="alert" class="range-error text-error text-caption">{{
      t('replay.invalidRange') }} {{ t('replay.timeFormat') }}</p>
    <div v-if="report" class="range-overview">
      <AnalyticsRangeOverview :range="range" :duration-ms="durationMs" :report="report" :timeline="timeline"
                              @range="emit('range', $event)"/>
      <div v-if="timelineLoading || timelineError" class="range-timeline-status text-caption muted">
        <span v-if="timelineLoading">{{ t('components.player.timelineLoading') }}</span>
        <template v-if="timelineError"><span role="alert">{{ timelineError }}</span>
          <v-btn type="button" size="x-small" variant="text" :disabled="timelineLoading"
                 @click="emit('retry-timeline')">{{ t('components.common.retry') }}
          </v-btn>
        </template>
      </div>
    </div>
    <v-progress-linear v-if="busy" indeterminate color="primary" :aria-label="t('replay.loadingRange')"
                       class="range-progress"/>
  </form>
</template>

<script setup lang="ts">
import {computed, ref, useId, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import type {AnalyticsRange} from '@/models/analyticsRange'
import type {Analytics} from '@/models/media'
import type {SessionTimeline} from '@/models/sessionTimeline'
import {formatDuration, formatTimecode} from '@/utilities/format'
import {parseTimecode} from '@/utilities/timeline'
import AnalyticsRangeOverview from './AnalyticsRangeOverview.vue'

const props = defineProps<{
  range?: AnalyticsRange;
  durationMs: number;
  busy?: boolean;
  pinSaving?: boolean;
  report?: Analytics;
  timeline?: SessionTimeline;
  timelineLoading?: boolean;
  timelineError?: string
}>()
const pinned = defineModel<boolean>('pinned', {default: true})
const emit = defineEmits<{ range: [value: AnalyticsRange | undefined]; 'retry-timeline': [] }>()
const {t} = useI18n()
const errorId = useId()
const startInput = ref('')
const endInput = ref('')
const invalidRange = ref(false)
const pinLabel = computed(() => t(pinned.value ? 'contributions.unpinRange' : 'contributions.pinRange'))
const rangeLabel = computed(() => props.range ? t('replay.selection', {
  from: formatDuration(props.range.fromMs, true),
  to: formatDuration(props.range.toMs, true)
}) : t('replay.fullSession'))
let capturedRange: AnalyticsRange = {fromMs: 0, toMs: 0}
let capturedStart = ''
let capturedEnd = ''

function synchronizeInputs(): void {
  capturedRange = props.range ?? {fromMs: 0, toMs: props.durationMs}
  capturedStart = formatTimecode(capturedRange.fromMs).replace(/\.000$/, '')
  capturedEnd = formatTimecode(capturedRange.toMs).replace(/\.000$/, '')
  startInput.value = capturedStart
  endInput.value = capturedEnd
  invalidRange.value = false
}

watch(() => [props.range?.fromMs, props.range?.toMs, props.durationMs], synchronizeInputs, {immediate: true})

function applyRange(): void {
  const fromMs = startInput.value.trim() === capturedStart ? capturedRange.fromMs : parseTimecode(startInput.value)
  const toMs = endInput.value.trim() === capturedEnd ? capturedRange.toMs : parseTimecode(endInput.value)
  if (fromMs === undefined || toMs === undefined || !Number.isFinite(fromMs) || !Number.isFinite(toMs) || fromMs < 0 || toMs > props.durationMs || toMs <= fromMs) {
    invalidRange.value = true
    return
  }
  invalidRange.value = false
  emit('range', fromMs === 0 && toMs === props.durationMs ? undefined : {fromMs, toMs})
}

function resetRange(): void {
  synchronizeInputs()
  emit('range', undefined)
}
</script>

<style scoped>
.analytics-range-toolbar {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px 12px;
  padding: 8px 12px;
  border: 1px solid rgba(var(--v-theme-on-surface), .12);
  border-radius: 12px;
  background: rgb(var(--v-theme-background));
  box-shadow: 0 6px 20px rgba(0, 0, 0, .12);
  position: relative;
  overflow: hidden;
}

.range-description {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-right: auto;
}

.range-description > .text-caption {
  line-height: 1.3;
}

.range-title {
  display: flex;
  align-items: center;
  gap: 5px;
  font-size: .8rem;
  font-weight: 600;
  white-space: nowrap;
}

.range-pin {
  width: 24px;
  height: 24px;
}

.range-fields, .range-actions {
  display: flex;
  align-items: center;
  gap: 6px;
}

.range-fields label {
  display: flex;
  align-items: center;
  gap: 5px;
  font-size: .68rem;
  color: rgba(var(--v-theme-on-surface), .6);
  white-space: nowrap;
}

.range-fields input {
  width: 120px;
  min-width: 0;
  padding: 4px 8px;
  height: 28px;
  border: 1px solid rgba(var(--v-theme-on-surface), .2);
  border-radius: 6px;
  color: rgb(var(--v-theme-on-surface));
  background: rgb(var(--v-theme-surface));
  font-size: .76rem;
}

.range-fields input:focus-visible {
  outline: 2px solid rgb(var(--v-theme-primary));
  outline-offset: 1px;
}

.range-fields input[aria-invalid="true"] {
  border-color: rgb(var(--v-theme-error));
}

.range-separator {
  color: rgba(var(--v-theme-on-surface), .4);
}

.range-actions :deep(.v-btn) {
  height: 28px;
}

.range-error {
  flex-basis: 100%;
}

.range-overview {
  flex-basis: 100%;
  min-width: 0;
}

.range-timeline-status {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin: 3px 6px 0;
}

.range-progress {
  position: absolute;
  bottom: 0;
  left: 0;
}

@media (max-width: 600px) {
  .analytics-range-toolbar {
    padding: 8px 10px;
    gap: 6px 8px;
  }

  .range-description {
    display: flex;
    flex-basis: 100%;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
  }

  .range-description > .text-caption {
    text-align: right;
  }

  .range-fields {
    flex: 1 1 100%;
  }

  .range-fields label {
    flex: 1;
    min-width: 0;
  }

  .range-fields input {
    width: 100%;
  }

  .range-actions {
    margin-left: auto;
  }
}
</style>
