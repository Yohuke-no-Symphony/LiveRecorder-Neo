<template>
  <v-dialog :model-value="!!stage" max-width="640" scrollable :aria-labelledby="titleId" @update:model-value="!$event && emit('close')">
    <ContentCard v-if="stage && task" :title="taskStageLabel(stage.id, task.type)" :title-id="titleId">
      <div class="stage-context">
        <strong class="stage-task-title">{{ task.title || t('components.library.unnamedSession') }}</strong>
        <StatusChip :status="stage.state" />
      </div>
      <TaskStages :stages="task.stages" :task-type="task.type" :selected-id="stage.id" expanded @select="selectedId = $event" />
      <v-alert v-if="task.workerOnline === false" type="warning" density="compact" class="mt-4">{{ t('taskHistory.workerOffline') }}<template v-if="task.lastUpdatedAtUtc"> · {{ formatDate(task.lastUpdatedAtUtc) }}</template></v-alert>
      <TaskStageDetails :stage="stage" :task-type="task.type" :error="taskStageError(stage, task.error)" />
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
import { formatDate } from '@/utilities/format'
import { taskKey, taskStageError, taskStageLabel } from '@/utilities/taskHistory'
import ContentCard from '@/components/common/ContentCard.vue'
import StatusChip from '@/components/common/StatusChip.vue'
import TaskStageDetails from './TaskStageDetails.vue'
import TaskStages from './TaskStages.vue'
import TaskStageLogs from './TaskStageLogs.vue'

const props = withDefaults(defineProps<{ task?: TaskSummary; stageId: string; scope?: TaskScope }>(), { scope: 'administration' })
const emit = defineEmits<{ close: [] }>()
const { t } = useI18n()
const auth = useAuthStore()
const titleId = useId()
const selectedId = ref(props.stageId)
const logsOpen = ref(false)
const stage = computed(() => props.task?.stages.find(value => value.id === selectedId.value))
watch([() => props.task && taskKey(props.task), () => props.stageId], () => { selectedId.value = props.stageId })
watch(() => props.task && taskKey(props.task), () => { logsOpen.value = false })
</script>

<style scoped>
.stage-context { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.stage-task-title { min-width: 0; overflow-wrap: anywhere; }
</style>
