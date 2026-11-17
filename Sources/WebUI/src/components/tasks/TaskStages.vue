<template>
  <ol v-if="stages.length" class="task-stages" :aria-label="t('taskHistory.stagesLabel')">
    <li v-for="stage in stages" :key="stage.id" class="task-stage-item">
      <button type="button" class="task-stage" :class="stageClass(stage.state)" :data-stage-id="stage.id" :data-stage-state="stage.state"
        :aria-label="`${taskStageLabel(stage.id, taskType)} · ${stageStateLabel(stage.state)}`" :title="`${taskStageLabel(stage.id, taskType)} · ${stageStateLabel(stage.state)}`"
        @click="emit('select', stage.id)">
        <v-icon :icon="stageIcon(stage.state)" size="16" :class="{ 'stage-spinning': stage.state === 'Running' }" />
      </button>
    </li>
  </ol>
  <span v-else class="text-caption muted">{{ t('taskHistory.waitingStages') }}</span>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { TaskStage } from '@/models/taskHistory'
import { taskStageLabel } from '@/utilities/taskHistory'

defineProps<{ stages: TaskStage[]; taskType?: string }>()
const emit = defineEmits<{ select: [id: string] }>()
const { t, te } = useI18n()
const stageStateLabel = (state: string) => te(`taskHistory.stageStates.${state}`) ? t(`taskHistory.stageStates.${state}`) : state
const stageClass = (state: string) => ({ Completed: 'stage-success', Succeeded: 'stage-success', Running: 'stage-running', Failed: 'stage-error', Interrupted: 'stage-warning', Cancelled: 'stage-muted', Skipped: 'stage-muted' }[state] ?? 'stage-pending')
const stageIcon = (state: string) => ({ Completed: 'mdi-check', Succeeded: 'mdi-check', Running: 'mdi-loading', Failed: 'mdi-close', Interrupted: 'mdi-pause', Cancelled: 'mdi-cancel', Skipped: 'mdi-minus' }[state] ?? 'mdi-circle-small')
</script>

<style scoped>
.task-stages { display: flex; align-items: center; flex-wrap: wrap; list-style: none; margin: 0; padding: 0; gap: 0; }
.task-stage-item { display: flex; align-items: center; }
.task-stage-item + .task-stage-item::before { content: ''; width: 12px; height: 2px; background: rgba(var(--v-theme-on-surface), .16); }
.task-stage { display: grid; place-items: center; width: 27px; height: 27px; border: 3px solid transparent; border-radius: 50%; cursor: pointer; transition: box-shadow .15s; padding: 0; }
.task-stage:focus-visible { outline: 2px solid rgb(var(--v-theme-primary)); outline-offset: 3px; }
.task-stage:hover { box-shadow: 0 0 0 3px rgba(var(--v-theme-primary), .14); }
.stage-success { color: rgb(var(--v-theme-surface)); background: rgb(var(--v-theme-success)); border-color: rgba(var(--v-theme-surface), .6); }
.stage-running { color: rgb(var(--v-theme-primary)); background: rgba(var(--v-theme-primary), .12); }
.stage-error { color: rgb(var(--v-theme-error)); background: rgba(var(--v-theme-error), .16); }
.stage-warning { color: rgb(var(--v-theme-warning)); background: rgba(var(--v-theme-warning), .16); }
.stage-muted, .stage-pending { color: rgba(var(--v-theme-on-surface), .5); background: rgba(var(--v-theme-on-surface), .07); }
.stage-spinning { animation: stage-spin 1.5s linear infinite; }
@keyframes stage-spin { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) { .stage-spinning { animation: none; } }
</style>
