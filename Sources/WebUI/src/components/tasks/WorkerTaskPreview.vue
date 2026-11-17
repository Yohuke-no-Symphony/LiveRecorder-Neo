<template>
  <div class="worker-task-preview">
    <div class="worker-task-heading"><span class="text-caption muted">{{ t('taskHistory.activeTasks') }}</span><a :href="`/admin/worker-tasks?workerId=${encodeURIComponent(workerId)}&state=active`" class="text-caption">{{ t('taskHistory.viewAll') }}<v-icon icon="mdi-chevron-right" size="14" /></a></div>
    <div v-for="task in tasks.slice(0, 3)" :key="taskKey(task)" class="worker-task-item" :data-task-id="task.id">
      <span class="worker-task-title" :title="task.title">{{ task.title || t('components.library.unnamedSession') }}</span>
      <StatusChip :status="task.state" />
      <TaskStages :stages="task.stages" :task-type="task.type" @select="emit('stage', task, $event)" />
    </div>
    <p v-if="!tasks.length" class="text-caption muted">{{ t('taskHistory.noActiveTasks') }}</p>
  </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { TaskSummary } from '@/models/taskHistory'
import { taskKey } from '@/utilities/taskHistory'
import StatusChip from '@/components/common/StatusChip.vue'
import TaskStages from './TaskStages.vue'

defineProps<{ workerId: string; tasks: TaskSummary[] }>()
const emit = defineEmits<{ stage: [task: TaskSummary, id: string] }>()
const { t } = useI18n()
</script>

<style scoped>
.worker-task-preview { border-top: 1px solid rgba(var(--v-theme-on-surface), .08); margin-top: 12px; padding-top: 10px; }
.worker-task-heading { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-bottom: 8px; }
.worker-task-heading a { color: rgb(var(--v-theme-primary)); white-space: nowrap; }
.worker-task-item { display: flex; align-items: center; flex-wrap: wrap; gap: 8px 12px; padding: 5px 0; }
.worker-task-title { flex: 1 1 160px; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: .8rem; }
@media (max-width: 600px) { .worker-task-item :deep(.task-stages) { flex-basis: 100%; } }
</style>
