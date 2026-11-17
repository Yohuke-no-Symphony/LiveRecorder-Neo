<template>
  <v-table class="task-table" density="compact">
    <thead><tr><th>{{ t('taskHistory.status') }}</th><th>{{ t('taskHistory.task') }}</th><th>{{ t('taskHistory.creator') }}</th><th>{{ t('taskHistory.stagesLabel') }}</th><th class="task-actions-heading">{{ t('taskHistory.actions') }}</th></tr></thead>
    <tbody>
      <tr v-for="task in items" :key="taskKey(task)" :data-job-id="task.id" :data-job-kind="task.kind">
        <td class="task-status">
          <StatusChip :status="task.state" />
          <div class="task-time mono" :title="t('taskHistory.duration')">{{ duration(task) }}</div>
          <time class="task-time" :datetime="task.createdAtUtc">{{ formatDate(task.createdAtUtc) }}</time>
        </td>
        <td class="task-description">
          <a v-if="task.canViewResults && task.sessionId" class="task-title" :title="task.title || t('components.library.unnamedSession')" :href="recordingUrl(task)" target="_blank" rel="noopener noreferrer">{{ task.title || t('components.library.unnamedSession') }}</a>
          <strong v-else class="task-title" :title="task.title || t('components.library.unnamedSession')">{{ task.title || t('components.library.unnamedSession') }}</strong>
          <div class="task-badges">
            <v-chip size="x-small">{{ taskTypeLabel(task.type) }}</v-chip>
            <v-chip size="x-small" variant="outlined">{{ t(`taskHistory.origins.${task.origin}`) }}</v-chip>
            <v-chip v-if="task.profileName" size="x-small">{{ task.profileName }}</v-chip>
            <v-chip v-if="task.mode" size="x-small">{{ t(`taskHistory.modes.${task.mode}`) }}</v-chip>
            <v-chip v-if="task.workerId" size="x-small" :color="task.workerOnline === false ? 'warning' : undefined" :title="task.workerOnline === false ? t('taskHistory.workerOffline') : undefined">{{ task.workerId === 'local' ? t('taskHistory.localWorker') : task.workerName || task.workerId }}<v-icon v-if="task.workerOnline === false" icon="mdi-lan-disconnect" size="12" class="ml-1" /></v-chip>
          </div>
        </td>
        <td class="task-creator">
          <div v-if="task.origin === 'User'" class="task-person"><UserAvatar :name="task.initiatorName || t('taskHistory.unknownCreator')" :avatar-url="task.initiatorAvatarUrl" size="26" /><span>{{ task.initiatorName || t('taskHistory.unknownCreator') }}</span></div>
          <div v-else class="task-person muted"><v-icon :icon="task.origin === 'Automatic' ? 'mdi-robot-outline' : 'mdi-history'" size="22" /><span>{{ t(task.origin === 'Automatic' ? 'taskHistory.systemCreator' : 'taskHistory.legacyCreator') }}</span></div>
        </td>
        <td class="task-stage-cell"><TaskStages :stages="task.stages" :task-type="task.type" @select="emit('stage', task, $event)" /></td>
        <td class="task-actions"><div class="action-buttons">
          <v-tooltip v-for="action in actions(task)" :key="action.key" :text="action.label"><template #activator="{ props: tooltipProps }">
            <v-btn v-bind="tooltipProps" class="job-action-button" :icon="action.icon" :href="action.href" :target="action.external ? '_blank' : undefined" :rel="action.external ? 'noopener noreferrer' : undefined" :color="action.color" variant="text" size="small" :aria-label="action.label" :disabled="!action.href && (!!actionKey || (action.key === 'cancel' && task.cancelRequested))" :loading="actionKey === `${taskKey(task)}:${action.key}`" @click="activate(task, action.key)" />
          </template></v-tooltip>
        </div></td>
      </tr>
    </tbody>
  </v-table>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAuthStore } from '@/stores/auth'
import type { TaskSummary } from '@/models/taskHistory'
import { taskKey, taskTypeLabel } from '@/utilities/taskHistory'
import { formatDate, formatDuration } from '@/utilities/format'
import StatusChip from '@/components/common/StatusChip.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'
import TaskStages from './TaskStages.vue'

defineProps<{ items: TaskSummary[]; actionKey?: string }>()
const emit = defineEmits<{ stage: [task: TaskSummary, id: string]; action: [task: TaskSummary, action: 'cancel' | 'retry']; delete: [task: TaskSummary] }>()
const { t } = useI18n()
const auth = useAuthStore()
interface TaskAction { key: string; label: string; icon: string; color?: string; href?: string; external?: boolean }
const recordingUrl = (task: TaskSummary) => `/sessions/${encodeURIComponent(task.sessionId)}`
function actions(task: TaskSummary): TaskAction[]
{
  const result: TaskAction[] = []
  if (task.canViewResults && task.kind === 'ai')
  {
    const markers = task.type === 'LiveMarkers'
    result.push({ key: 'results', label: t(markers ? 'taskHistory.viewMarkers' : 'taskHistory.viewAnalysis'),
      icon: markers ? 'mdi-format-list-bulleted' : 'mdi-chart-box-outline', external: true,
      href: markers ? `${recordingUrl(task)}?panel=markers` : `/analytics?sessionId=${encodeURIComponent(task.sessionId)}` })
  }
  if (task.canCancel) { result.push({ key: 'cancel', label: t(task.cancelRequested ? 'taskHistory.cancelling' : 'taskHistory.cancel'), icon: 'mdi-close' }) }
  if (task.canRetry) { result.push({ key: 'retry', label: t('taskHistory.retry'), icon: 'mdi-refresh', color: 'primary' }) }
  if (task.type === 'Merge' && task.state === 'Completed' && auth.canDownloadRecordings)
  {
    for (let index = 0; index < task.outputCount; index++)
    {
      result.push({ key: `download-${index}`, label: task.outputCount > 1 ? t('taskHistory.downloadNumbered', { number: index + 1 }) : t('taskHistory.download'), icon: 'mdi-download', href: MediaAPI.downloadJobOutput(task.id, index) })
    }
  }
  if (task.canDelete) { result.push({ key: 'delete', label: t('taskHistory.delete'), icon: 'mdi-delete-outline', color: 'error' }) }
  return result
}
function activate(task: TaskSummary, action: string): void
{
  if (action === 'cancel' || action === 'retry') { emit('action', task, action) }
  else if (action === 'delete') { emit('delete', task) }
}
function duration(task: TaskSummary): string
{
  const start = Date.parse(task.createdAtUtc)
  const finish = task.finishedAtUtc ? Date.parse(task.finishedAtUtc) : Date.now()
  return Number.isFinite(start) && Number.isFinite(finish) ? formatDuration(Math.max(0, finish - start), true) : '—'
}
</script>

<style scoped>
.task-table :deep(table) { width: 100%; table-layout: fixed; }
.task-table :deep(th:first-child) { width: 170px; }
.task-table :deep(th:nth-child(2)) { width: 32%; }
.task-table :deep(th:nth-child(3)) { width: 150px; }
.task-table :deep(th:nth-child(4)) { width: 235px; }
.task-table :deep(td) { padding: 12px 16px !important; vertical-align: middle; }
.task-title { display: block; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
a.task-title:hover { color: rgb(var(--v-theme-primary)); }
.task-badges { display: flex; flex-wrap: wrap; gap: 4px; margin-top: 6px; }
.task-time { display: block; font-size: .7rem; color: rgba(var(--v-theme-on-surface), .56); margin-top: 5px; white-space: nowrap; }
.task-person { display: flex; align-items: center; gap: 7px; font-size: .8rem; min-width: 0; }
.task-person span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.task-actions .action-buttons { justify-content: flex-end; flex-wrap: wrap; }
.task-actions-heading { text-align: right !important; }
@media (max-width: 960px) {
  .task-table :deep(thead) { display: none; }
  .task-table :deep(table), .task-table :deep(tbody) { display: block; }
  .task-table :deep(tr) { display: grid; grid-template-columns: minmax(0, 1fr) auto; padding: 12px 16px; gap: 10px 12px; border-bottom: 1px solid rgba(var(--v-theme-on-surface), .08); }
  .task-table :deep(td) { display: block; padding: 0 !important; border: 0 !important; min-width: 0; height: auto !important; }
  .task-description { grid-column: 1 / -1; grid-row: 1; }
  .task-status { grid-column: 1; grid-row: 2; }
  .task-creator { grid-column: 2; grid-row: 2; align-self: start; max-width: 150px; }
  .task-stage-cell { grid-column: 1; grid-row: 3; align-self: center; }
  .task-actions { grid-column: 2; grid-row: 3; }
}
@media (max-width: 600px) {
  .task-table :deep(tr) { grid-template-columns: minmax(0, 1fr); }
  .task-creator { grid-column: 1; grid-row: 3; }
  .task-stage-cell { grid-column: 1; grid-row: 4; }
  .task-actions { grid-column: 1; grid-row: 5; }
}
</style>
