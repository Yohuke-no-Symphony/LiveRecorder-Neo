<template>
  <div class="task-stage-overview">
    <ol v-if="stages.length" class="task-stages" :class="{ 'task-stages-expanded': expanded }" :aria-label="t('taskHistory.stagesLabel')">
      <li v-for="stage in stages" :key="stage.id" class="task-stage-item">
        <button type="button" class="task-stage-button" :class="{ 'task-stage-selected': selectedId === stage.id }" :data-stage-id="stage.id" :data-stage-state="stage.state"
          :aria-pressed="selectedId ? selectedId === stage.id : undefined" :aria-label="stageLabel(stage)" :title="stageLabel(stage)"
          @click="emit('select', stage.id)">
          <span class="task-stage" :class="stageClass(stage.state)"><v-icon :icon="stageIcon(stage.state)" size="16" :class="{ 'stage-spinning': stage.state === 'Running' }" /></span>
          <span v-if="expanded" class="stage-node-name">{{ taskStageLabel(stage.id, taskType) }}</span>
          <span v-if="expanded" class="stage-node-progress mono">{{ taskStageProgress(stage) }}</span>
          <v-progress-linear v-if="expanded" class="stage-node-bar" :model-value="stage.progress ?? 0" :indeterminate="stage.state === 'Running' && stage.progress == null" :color="stageColor(stage.state)" height="3" rounded :aria-label="stageLabel(stage)" />
        </button>
      </li>
    </ol>
    <div v-if="!expanded && currentStages.length" class="current-stages">
      <button v-for="stage in currentStages" :key="stage.id" type="button" class="current-stage" :aria-label="stageLabel(stage)" @click="emit('select', stage.id)">
        <span class="current-stage-heading"><span>{{ taskStageLabel(stage.id, taskType) }}</span><span class="mono">{{ taskStageProgress(stage) }}</span></span>
        <v-progress-linear :model-value="stage.progress ?? 0" :indeterminate="stage.state === 'Running' && stage.progress == null" :color="stageColor(stage.state)" height="3" rounded :aria-label="stageLabel(stage)" />
      </button>
    </div>
    <span v-if="!stages.length" class="text-caption muted">{{ t('taskHistory.waitingStages') }}</span>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { TaskStage } from '@/models/taskHistory'
import { taskStageLabel, taskStageProgress } from '@/utilities/taskHistory'

const props = defineProps<{ stages: TaskStage[]; taskType?: string; expanded?: boolean; selectedId?: string }>()
const emit = defineEmits<{ select: [id: string] }>()
const { t, te } = useI18n()
const stageStateLabel = (state: string) => te(`taskHistory.stageStates.${state}`) ? t(`taskHistory.stageStates.${state}`) : state
const stageLabel = (stage: TaskStage) => `${taskStageLabel(stage.id, props.taskType)} · ${stageStateLabel(stage.state)} · ${taskStageProgress(stage)}`
const stageColor = (state: string) => state === 'Failed' ? 'error' : state === 'Completed' || state === 'Succeeded' ? 'success' : 'primary'
const currentStages = computed(() =>
{
  const active = props.stages.filter(stage => stage.state === 'Running' || stage.state === 'Waiting')
  if (active.length) { return active }
  const last = props.stages.find(stage => stage.state === 'Failed' || stage.state === 'Interrupted')
    ?? [...props.stages].reverse().find(stage => stage.state !== 'Pending' && stage.state !== 'Queued')
  return last ? [last] : []
})
const stageClass = (state: string) => ({ Completed: 'stage-success', Succeeded: 'stage-success', Running: 'stage-running', Failed: 'stage-error', Interrupted: 'stage-warning', Cancelled: 'stage-muted', Skipped: 'stage-muted' }[state] ?? 'stage-pending')
const stageIcon = (state: string) => ({ Completed: 'mdi-check', Succeeded: 'mdi-check', Running: 'mdi-loading', Failed: 'mdi-close', Interrupted: 'mdi-pause', Cancelled: 'mdi-cancel', Skipped: 'mdi-minus' }[state] ?? 'mdi-circle-small')
</script>

<style scoped>
.task-stage-overview { min-width: 0; }
.task-stages { display: flex; align-items: center; flex-wrap: wrap; list-style: none; margin: 0; padding: 0; gap: 0; }
.task-stage-item { display: flex; align-items: center; }
.task-stage-item + .task-stage-item::before { content: ''; width: 12px; height: 2px; background: rgba(var(--v-theme-on-surface), .16); }
.task-stage-button { display: grid; justify-items: center; gap: 5px; padding: 0; color: inherit; border-radius: 8px; cursor: pointer; min-width: 0; }
.task-stage-button:focus-visible, .current-stage:focus-visible { outline: 2px solid rgb(var(--v-theme-primary)); outline-offset: 3px; }
.task-stage { display: grid; place-items: center; width: 27px; height: 27px; border: 3px solid transparent; border-radius: 50%; transition: box-shadow .15s; }
.task-stage-button:hover .task-stage, .task-stage-selected .task-stage { box-shadow: 0 0 0 3px rgba(var(--v-theme-primary), .24); }
.task-stages-expanded { align-items: stretch; gap: 8px 0; margin: 16px 0 20px; }
.task-stages-expanded .task-stage-item { flex: 1 1 86px; align-items: start; min-width: 0; }
.task-stages-expanded .task-stage-item + .task-stage-item::before { flex-shrink: 0; width: 10px; margin-top: 16px; }
.task-stages-expanded .task-stage-button { flex: 1; padding: 5px 6px 8px; }
.task-stages-expanded .task-stage-selected { background: rgba(var(--v-theme-primary), .08); }
.stage-node-name { font-size: .75rem; line-height: 1.4; overflow-wrap: anywhere; }
.stage-node-progress { font-size: .7rem; color: rgba(var(--v-theme-on-surface), .6); }
.stage-node-bar { width: 100%; }
.current-stages { display: grid; gap: 8px; margin-top: 9px; max-width: 235px; }
.current-stage { display: grid; gap: 5px; text-align: left; color: inherit; min-width: 0; width: 100%; cursor: pointer; }
.current-stage-heading { display: flex; justify-content: space-between; gap: 12px; font-size: .72rem; }
.stage-success { color: rgb(var(--v-theme-surface)); background: rgb(var(--v-theme-success)); border-color: rgba(var(--v-theme-surface), .6); }
.stage-running { color: rgb(var(--v-theme-primary)); background: rgba(var(--v-theme-primary), .12); }
.stage-error { color: rgb(var(--v-theme-error)); background: rgba(var(--v-theme-error), .16); }
.stage-warning { color: rgb(var(--v-theme-warning)); background: rgba(var(--v-theme-warning), .16); }
.stage-muted, .stage-pending { color: rgba(var(--v-theme-on-surface), .5); background: rgba(var(--v-theme-on-surface), .07); }
.stage-spinning { animation: stage-spin 1.5s linear infinite; }
@keyframes stage-spin { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) { .stage-spinning { animation: none; } }
</style>
