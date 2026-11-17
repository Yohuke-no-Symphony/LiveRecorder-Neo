<template>
  <section class="stage-logs" :aria-label="title">
    <h3 class="section-heading">{{ title }}</h3>
    <WorkerLogViewer :entries="entries" :loading="loading" :error="error" :retrying="retrying" :title="title" :stream-key="identity ?? ''" :show-job="false" :empty-text="t('taskHistory.logsEmpty')" compact />
  </section>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { TaskHistoryAPI } from '@/apis/TaskHistoryAPI'
import { useWorkerLogs } from '@/composables/useWorkerLogs'
import type { TaskScope, TaskSummary } from '@/models/taskHistory'
import { useAuthStore } from '@/stores/auth'
import { taskStageLabel } from '@/utilities/taskHistory'
import WorkerLogViewer from '@/components/common/WorkerLogViewer.vue'

const props = defineProps<{ task: TaskSummary; stageId: string; scope: TaskScope }>()
const auth = useAuthStore()
const { t } = useI18n()
const title = computed(() => t('taskHistory.stageLogs', { stage: taskStageLabel(props.stageId, props.task.type) }))
const identity = computed(() => auth.hasAccess && auth.isAdmin
  ? JSON.stringify([props.scope, props.task.kind, props.task.id, props.task.attempt, props.stageId, auth.user?.id, auth.user?.status]) : null)
const { entries, loading, error, retrying } = useWorkerLogs(() => identity.value,
  (after, signal) => TaskHistoryAPI.logs(props.scope, props.task, props.stageId, after, signal))
</script>

<style scoped>
.stage-logs { margin-top: 20px; }
</style>
