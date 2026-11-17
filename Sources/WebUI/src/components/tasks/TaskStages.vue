<template>
  <div class="task-stage-overview">
    <ol v-if="stages.length" class="task-stages" :class="{ 'task-stages-expanded': expanded }" :aria-label="t('taskHistory.stagesLabel')">
      <li v-for="stage in stages" :key="stage.id" class="task-stage-item">
        <v-tooltip :text="stageLabel(stage)" location="top"><template #activator="{ props: tooltipProps }">
          <button v-bind="tooltipProps" type="button" class="task-stage-button" :class="{ 'task-stage-selected': selectedId === stage.id }" :data-stage-id="stage.id" :data-stage-state="stage.state"
            :aria-pressed="selectedId ? selectedId === stage.id : undefined" :aria-label="stageLabel(stage)"
            @click="emit('select', stage.id)">
            <span class="task-stage" :class="stageClass(stage.state)">
              <v-progress-circular class="stage-progress-ring" :model-value="stage.progress ?? 0" :indeterminate="stage.state === 'Running' && stage.progress == null" :color="stageColor(stage.state)" size="30" width="2" aria-hidden="true" />
              <v-icon class="stage-state-icon" :icon="stageIcon(stage.state)" size="15" />
            </span>
            <span v-if="expanded" class="stage-node-name">{{ taskStageLabel(stage.id, taskType) }}</span>
          </button>
        </template></v-tooltip>
      </li>
    </ol>
    <span v-if="!stages.length" class="text-caption muted">{{ t('taskHistory.waitingStages') }}</span>
  </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { TaskStage } from '@/models/taskHistory'
import { taskStageLabel, taskStageProgress } from '@/utilities/taskHistory'

const props = defineProps<{ stages: TaskStage[]; taskType?: string; expanded?: boolean; selectedId?: string }>()
const emit = defineEmits<{ select: [id: string] }>()
const { t, te } = useI18n()
const stageStateLabel = (state: string) => te(`taskHistory.stageStates.${state}`) ? t(`taskHistory.stageStates.${state}`) : state
const stageLabel = (stage: TaskStage) => `${taskStageLabel(stage.id, props.taskType)} · ${stageStateLabel(stage.state)} · ${taskStageProgress(stage)}`
const stageColor = (state: string) => ({ Failed: 'error', Interrupted: 'warning', Completed: 'success', Succeeded: 'success', Running: 'primary' }[state] ?? 'secondary')
const stageClass = (state: string) => ({ Completed: 'stage-success', Succeeded: 'stage-success', Running: 'stage-running', Failed: 'stage-error', Interrupted: 'stage-warning', Cancelled: 'stage-muted', Skipped: 'stage-muted' }[state] ?? 'stage-pending')
const stageIcon = (state: string) => ({ Completed: 'mdi-check', Succeeded: 'mdi-check', Running: 'mdi-play', Failed: 'mdi-close', Interrupted: 'mdi-pause', Cancelled: 'mdi-cancel', Skipped: 'mdi-minus' }[state] ?? 'mdi-circle-small')
</script>

<style scoped>
.task-stage-overview { min-width: 0; }
.task-stages { display: flex; align-items: center; flex-wrap: wrap; list-style: none; margin: 0; padding: 0; gap: 0; }
.task-stage-item { display: flex; align-items: center; }
.task-stage-item + .task-stage-item::before { content: ''; width: 12px; height: 2px; background: rgba(var(--v-theme-on-surface), .16); }
.task-stage-button { display: grid; justify-items: center; gap: 5px; padding: 0; color: inherit; border-radius: 8px; cursor: pointer; min-width: 0; }
.task-stage-button:focus-visible { outline: 2px solid rgb(var(--v-theme-primary)); outline-offset: 3px; }
.task-stage { position: relative; display: grid; place-items: center; width: 30px; height: 30px; border-radius: 50%; background: rgb(var(--v-theme-surface)); transition: box-shadow .15s; }
.stage-progress-ring { position: absolute; inset: 0; }
.stage-state-icon { position: relative; }
.task-stage-button:hover .task-stage, .task-stage-selected .task-stage { box-shadow: 0 0 0 3px rgba(var(--v-theme-primary), .24); }
.task-stages-expanded { align-items: stretch; flex-wrap: nowrap; overflow-x: auto; padding: 4px 3px; margin: 16px 0; }
.task-stages-expanded .task-stage-item { position: relative; flex: 1 0 84px; align-items: start; min-width: 0; }
.task-stages-expanded .task-stage-item + .task-stage-item::before { position: absolute; top: 19px; left: -50%; width: 100%; }
.task-stages-expanded .task-stage-button { position: relative; z-index: 1; flex: 1; padding: 5px 6px 8px; }
.task-stages-expanded .task-stage-selected { background: rgba(var(--v-theme-primary), .08); }
.stage-node-name { font-size: .75rem; line-height: 1.4; overflow-wrap: anywhere; }
.stage-success { color: rgb(var(--v-theme-surface)); background: rgb(var(--v-theme-success)); }
.stage-running { color: rgb(var(--v-theme-primary)); }
.stage-error { color: rgb(var(--v-theme-error)); }
.stage-warning { color: rgb(var(--v-theme-warning)); }
.stage-muted, .stage-pending { color: rgba(var(--v-theme-on-surface), .5); }
</style>
