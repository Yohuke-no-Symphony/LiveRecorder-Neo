<template>
  <div>
    <div v-if="stage.substate && te(`taskHistory.substates.${stage.substate}`)" class="text-caption muted mt-2">{{
      t(`taskHistory.substates.${stage.substate}`) }}
    </div>
    <div class="stage-progress">
      <div class="d-flex justify-space-between align-center ga-2 mb-2"><span>{{ taskStageLabel(stage.id, taskType) }} · {{ t('taskHistory.stageProgress') }}</span><span
        class="mono text-no-wrap">{{ taskStageProgress(stage) }}</span></div>
      <v-progress-linear :model-value="stage.progress ?? 0"
                         :indeterminate="stage.state === 'Running' && stage.progress == null"
                         :color="stage.state === 'Failed' ? 'error' : stage.state === 'Completed' ? 'success' : 'primary'"
                         height="6" rounded :aria-label="t('taskHistory.stageProgress')"/>
    </div>
    <dl class="stage-metrics">
      <div v-if="stage.startedAtUtc">
        <dt>{{ t('taskHistory.startedAt') }}</dt>
        <dd>{{ formatDate(stage.startedAtUtc) }}</dd>
      </div>
      <div v-if="stage.finishedAtUtc">
        <dt>{{ t('taskHistory.finishedAt') }}</dt>
        <dd>{{ formatDate(stage.finishedAtUtc) }}</dd>
      </div>
      <div v-if="stage.elapsedMs != null">
        <dt>{{ t('taskHistory.duration') }}</dt>
        <dd>{{ formatDuration(stage.elapsedMs, true) }}</dd>
      </div>
      <div v-for="metric in stage.metrics" :key="metric.key">
        <dt>{{ te(`taskHistory.metrics.${metric.key}`) ? t(`taskHistory.metrics.${metric.key}`) : metric.key }}</dt>
        <dd>{{ taskMetricValue(metric) }}</dd>
      </div>
    </dl>
    <v-alert v-if="error" :type="stage.state === 'Failed' ? 'error' : 'warning'" density="compact" class="mt-4">{{
      taskErrorLabel(error) }}
    </v-alert>
  </div>
</template>

<script setup lang="ts">
import {useI18n} from 'vue-i18n'
import type {TaskStage} from '@/models/taskHistory'
import {formatDate, formatDuration} from '@/utilities/format'
import {taskErrorLabel, taskMetricValue, taskStageLabel, taskStageProgress} from '@/utilities/taskHistory'

defineProps<{ stage: TaskStage; taskType: string; error?: string }>()
const {t, te} = useI18n()
</script>

<style scoped>
.stage-progress {
  margin: 24px 0;
}

.stage-metrics {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px 24px;
  margin: 0;
}

.stage-metrics > div {
  min-width: 0;
}

.stage-metrics dt {
  color: rgba(var(--v-theme-on-surface), .6);
  font-size: .78rem;
  margin-bottom: 4px;
}

.stage-metrics dd {
  margin: 0;
  overflow-wrap: anywhere;
  font-variant-numeric: tabular-nums;
}

@media (max-width: 600px) {
  .stage-metrics {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
