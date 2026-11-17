<template>
  <v-dialog :model-value="!!stage" max-width="640" scrollable :aria-labelledby="titleId" @update:model-value="!$event && emit('close')">
    <ContentCard v-if="stage && task" :title="taskStageLabel(stage.id, task.type)" :title-id="titleId">
      <div class="stage-context">
        <strong class="stage-task-title">{{ task.title || t('components.library.unnamedSession') }}</strong>
        <StatusChip :status="stage.state" />
      </div>
      <TaskStages :stages="task.stages" :task-type="task.type" :selected-id="stage.id" expanded @select="selectedId = $event" />
      <div v-if="stage.substate && te(`taskHistory.substates.${stage.substate}`)" class="text-caption muted mt-2">{{ t(`taskHistory.substates.${stage.substate}`) }}</div>
      <v-alert v-if="task.workerOnline === false" type="warning" density="compact" class="mt-4">{{ t('taskHistory.workerOffline') }}<template v-if="task.lastUpdatedAtUtc"> · {{ formatDate(task.lastUpdatedAtUtc) }}</template></v-alert>
      <div class="stage-progress">
        <div class="d-flex justify-space-between align-center mb-2"><span>{{ taskStageLabel(stage.id, task.type) }} · {{ t('taskHistory.stageProgress') }}</span><span class="mono">{{ taskStageProgress(stage) }}</span></div>
        <v-progress-linear :model-value="stage.progress ?? 0" :indeterminate="stage.state === 'Running' && stage.progress == null" :color="stage.state === 'Failed' ? 'error' : stage.state === 'Completed' ? 'success' : 'primary'" height="6" rounded :aria-label="t('taskHistory.stageProgress')" />
      </div>
      <dl class="stage-metrics">
        <div v-if="stage.startedAtUtc"><dt>{{ t('taskHistory.startedAt') }}</dt><dd>{{ formatDate(stage.startedAtUtc) }}</dd></div>
        <div v-if="stage.finishedAtUtc"><dt>{{ t('taskHistory.finishedAt') }}</dt><dd>{{ formatDate(stage.finishedAtUtc) }}</dd></div>
        <div v-if="stage.elapsedMs !== null && stage.elapsedMs !== undefined"><dt>{{ t('taskHistory.duration') }}</dt><dd>{{ formatDuration(stage.elapsedMs, true) }}</dd></div>
        <div v-for="metric in stage.metrics" :key="metric.key"><dt>{{ te(`taskHistory.metrics.${metric.key}`) ? t(`taskHistory.metrics.${metric.key}`) : metric.key }}</dt><dd>{{ taskMetricValue(metric) }}</dd></div>
      </dl>
      <v-alert v-if="stageError" :type="stage.state === 'Failed' ? 'error' : 'warning'" density="compact" class="mt-4">{{ taskErrorLabel(stageError) }}</v-alert>
      <TaskStageLogs v-if="logsOpen && auth.isAdmin" :task="task" :stage-id="stage.id" :scope="scope" />
      <template #actions>
        <v-btn v-if="auth.isAdmin" variant="text" prepend-icon="mdi-text-box-search-outline" :aria-expanded="logsOpen" @click="logsOpen = !logsOpen">{{ t(logsOpen ? 'taskHistory.hideLogs' : 'taskHistory.viewLogs') }}</v-btn>
        <v-btn variant="text" @click="emit('close')">{{ t('views.common.close') }}</v-btn>
      </template>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import { computed, ref, useId, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import type { TaskScope, TaskSummary } from '@/models/taskHistory'
import { useAuthStore } from '@/stores/auth'
import { formatDate, formatDuration } from '@/utilities/format'
import { taskErrorLabel, taskKey, taskMetricValue, taskStageLabel, taskStageProgress } from '@/utilities/taskHistory'
import ContentCard from '@/components/common/ContentCard.vue'
import StatusChip from '@/components/common/StatusChip.vue'
import TaskStages from './TaskStages.vue'
import TaskStageLogs from './TaskStageLogs.vue'

const props = withDefaults(defineProps<{ task?: TaskSummary; stageId: string; scope?: TaskScope }>(), { scope: 'administration' })
const emit = defineEmits<{ close: [] }>()
const { t, te } = useI18n()
const auth = useAuthStore()
const titleId = useId()
const selectedId = ref(props.stageId)
const logsOpen = ref(false)
const stage = computed(() => props.task?.stages.find(value => value.id === selectedId.value))
const stageError = computed(() =>
{
  if (!stage.value || ['Completed', 'Succeeded', 'Skipped', 'Pending', 'Queued'].includes(stage.value.state)) { return '' }
  return stage.value.error || (['Failed', 'Interrupted', 'Partial'].includes(stage.value.state) ? props.task?.error : '') || ''
})
watch([() => props.task && taskKey(props.task), () => props.stageId], () => { selectedId.value = props.stageId })
watch(() => props.task && taskKey(props.task), () => { logsOpen.value = false })
</script>

<style scoped>
.stage-context { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.stage-task-title { min-width: 0; overflow-wrap: anywhere; }
.stage-progress { margin: 24px 0; }
.stage-metrics { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px 24px; margin: 0; }
.stage-metrics > div { min-width: 0; }
.stage-metrics dt { color: rgba(var(--v-theme-on-surface), .6); font-size: .78rem; margin-bottom: 4px; }
.stage-metrics dd { margin: 0; overflow-wrap: anywhere; font-variant-numeric: tabular-nums; }
@media (max-width: 600px) { .stage-metrics { grid-template-columns: minmax(0, 1fr); } }
</style>
