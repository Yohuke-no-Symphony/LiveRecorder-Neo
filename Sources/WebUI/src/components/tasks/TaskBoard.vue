<template>
  <PageHeader :title="t(administration ? 'taskHistory.administrationTitle' : 'taskHistory.personalTitle')">
    <v-btn variant="outlined" color="error" prepend-icon="mdi-delete-sweep-outline" :disabled="!!actionKey || loading"
           :loading="actionKey === 'preview'" @click="previewClear">{{ t('taskHistory.clearCompleted') }}
    </v-btn>
    <v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="loading" @click="refresh">{{ t('taskHistory.refresh')
      }}
    </v-btn>
  </PageHeader>
  <v-alert v-if="pageActionError || error || filterError" type="error" density="compact" class="mb-4">{{ pageActionError
    || error || filterError }}
  </v-alert>
  <v-alert v-if="notice" type="info" density="compact" class="mb-4">{{ notice }}</v-alert>
  <div class="task-board">
    <div class="task-filters">
      <v-text-field v-model="searchDraft" :label="t('taskHistory.search')" prepend-inner-icon="mdi-magnify" clearable
                    density="compact" hide-details/>
      <v-select v-model="state" :items="stateOptions" :label="t('taskHistory.status')" density="compact" hide-details/>
      <v-select v-model="type" :items="typeOptions" :label="t('taskHistory.type')" density="compact" hide-details/>
      <v-select v-if="administration" v-model="origin" :items="originOptions" :label="t('taskHistory.origin')"
                density="compact" hide-details/>
      <v-select v-if="administration" v-model="worker" :items="workerOptions" :label="t('taskHistory.worker')"
                density="compact" hide-details/>
      <v-select v-if="administration" v-model="creator" :items="creatorOptions" :label="t('taskHistory.creator')"
                density="compact" hide-details/>
    </div>
    <div class="task-count text-caption muted" aria-live="polite">{{ t('taskHistory.count', { count: formatNumber(total)
      }) }}
    </div>
    <TaskTable v-if="items.length" :items="items" :action-key="actionKey" :administration="administration"
               @stage="openStage" @action="act" @delete="deleteTarget = $event" @interrupt="openRecordingInterruption"
               @abandon-upload="openRecordingUploadAbandonment"/>
    <div v-else class="task-empty muted" :role="loading ? 'status' : undefined">{{ t(loading ? 'taskHistory.loading' :
      'taskHistory.empty') }}
    </div>
    <nav class="task-pagination" :aria-label="t('taskHistory.pagination')">
      <v-btn variant="text" prepend-icon="mdi-chevron-left" :disabled="!cursors.length || loading"
             @click="previousPage">{{ t('taskHistory.previous') }}
      </v-btn>
      <span class="text-caption mono">{{ t('taskHistory.page', { page: formatNumber(page) }) }}</span>
      <v-btn variant="text" append-icon="mdi-chevron-right" :disabled="!nextCursor || loading" @click="nextPage">{{
        t('taskHistory.next') }}
      </v-btn>
    </nav>
  </div>
  <TaskStageDetailsDialog :task="selectedTask" :stage-id="selectedStageId" :scope="scope"
                          @close="selectedTask = undefined"/>
  <ConfirmDialog :model-value="!!deleteTarget" :title="t('taskHistory.deleteTitle')"
                 :message="t('taskHistory.deleteMessage', { title: deleteTarget?.title || t('components.library.unnamedSession') })"
                 :action-label="t('taskHistory.delete')" danger :busy="!!actionKey" :error="actionError"
                 @update:model-value="!$event && !actionKey && (deleteTarget = undefined)" @confirm="dismiss"/>
  <ConfirmDialog :model-value="!!interruptTarget" :title="t('taskHistory.interruptRecording')"
                 :message="t('taskHistory.interruptRecordingMessage', { title: interruptTarget?.title || t('components.library.unnamedSession'), started: formatDate(interruptTarget?.createdAtUtc) })"
                 :action-label="t('taskHistory.interruptRecording')" danger :busy="!!actionKey" :error="actionError"
                 @update:model-value="!$event && closeRecordingInterruption()" @confirm="interruptRecording"/>
  <ConfirmDialog :model-value="!!abandonUploadTarget" :title="t('taskHistory.abandonRecordingUpload')"
                 :message="t('taskHistory.abandonRecordingUploadMessage', { title: abandonUploadTarget?.title || t('components.library.unnamedSession'), started: formatDate(abandonUploadTarget?.createdAtUtc) })"
                 :action-label="t('taskHistory.abandonRecordingUpload')" danger :busy="!!actionKey" :error="actionError"
                 @update:model-value="!$event && closeRecordingUploadAbandonment()" @confirm="abandonRecordingUpload"/>
  <ConfirmDialog :model-value="!!clearPreview" :title="t('taskHistory.clearTitle')"
                 :message="`${t('taskHistory.clearMessage', { count: formatNumber(clearPreview?.count ?? 0) })} · ${frozenFilterLabel}`"
                 :action-label="t('taskHistory.clearCompleted')" danger :busy="!!actionKey" :error="actionError"
                 @update:model-value="!$event && !actionKey && closeClear()" @confirm="clearCompleted"/>
</template>

<script setup lang="ts">
import {computed, onScopeDispose, ref, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import {useRoute} from 'vue-router'
import {TaskHistoryAPI} from '@/apis/TaskHistoryAPI'
import {useTaskHistory} from '@/composables/useTaskHistory'
import {useAuthStore} from '@/stores/auth'
import type {TaskFilters, TaskQuery, TaskScope} from '@/models/taskHistory'
import {formatDate, formatNumber} from '@/utilities/format'
import {taskTypeLabel} from '@/utilities/taskHistory'
import PageHeader from '@/components/common/PageHeader.vue'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import TaskTable from './TaskTable.vue'
import TaskStageDetailsDialog from './TaskStageDetailsDialog.vue'

const props = withDefaults(defineProps<{ scope?: TaskScope }>(), {scope: 'personal'})
const {t} = useI18n()
const auth = useAuthStore()
const route = useRoute()
const administration = computed(() => props.scope === 'administration')
const stateFromQuery = (value: unknown) => typeof value === 'string' && ['all', 'active', 'Completed', 'failed', 'Cancelled', 'Skipped'].includes(value) ? value : 'all'
const state = ref(stateFromQuery(route.query.state))
const type = ref('all')
const origin = ref('all')
const worker = ref(typeof route.query.workerId === 'string' ? route.query.workerId : 'all')
const creator = ref('all')
const searchDraft = ref<string | null>('')
const search = ref('')
const filterData = ref<TaskFilters>({initiators: [], workers: []})
const filterError = ref('')
const filters = computed<TaskQuery>(() => ({
  state: state.value, type: type.value === 'all' ? undefined : type.value,
  origin: administration.value && origin.value !== 'all' ? origin.value : undefined,
  workerId: administration.value && worker.value !== 'all' ? worker.value : undefined,
  initiatorUserId: administration.value && creator.value !== 'all' ? creator.value : undefined,
  search: search.value || undefined, limit: 50
}))
const {
  items, total, loading, error, actionError, notice, actionKey, page, nextCursor, cursors,
  selectedTask, selectedStageId, deleteTarget, interruptTarget, abandonUploadTarget, clearPreview, clearFilter, refresh, act, dismiss,
  openRecordingInterruption, closeRecordingInterruption, interruptRecording,
  openRecordingUploadAbandonment, closeRecordingUploadAbandonment, abandonRecordingUpload, previewClear, clearCompleted,
  openStage, nextPage, previousPage
} = useTaskHistory(() => props.scope, () => filters.value)
const pageActionError = computed(() => deleteTarget.value || interruptTarget.value || abandonUploadTarget.value || clearPreview.value ? '' : actionError.value)
const stateOptions = computed(() => ['all', 'active', 'Completed', 'failed', 'Cancelled', 'Skipped'].map(value => ({
  value,
  title: t(`taskHistory.states.${value}`)
})))
const typeOptions = computed(() => [{value: 'all', title: t('taskHistory.all')}, ...['Clip', 'Analysis', 'LiveMarkers',
  ...(auth.isAdmin ? ['Transcode', 'Repair', 'Merge'] : []), ...(administration.value ? ['Recording'] : [])]
  .map(value => ({value, title: taskTypeLabel(value)}))])
const originOptions = computed(() => ['all', 'User', 'Automatic', 'Legacy'].map(value => ({
  value,
  title: value === 'all' ? t('taskHistory.all') : t(`taskHistory.origins.${value}`)
})))
const workerOptions = computed(() => [{value: 'all', title: t('taskHistory.all')}, {
  value: 'local',
  title: t('taskHistory.localWorker')
}, ...filterData.value.workers.map(value => ({value: value.id, title: value.name}))])
const creatorOptions = computed(() => [{
  value: 'all',
  title: t('taskHistory.all')
}, ...filterData.value.initiators.map(value => ({value: value.id, title: value.name}))])
const frozenFilterLabel = computed(() => {
  const value = clearFilter.value
  if (!value) {
    return ''
  }
  return [value.type && taskTypeLabel(value.type), value.origin && t(`taskHistory.origins.${value.origin}`),
    value.workerId && (workerOptions.value.find(option => option.value === value.workerId)?.title || value.workerId),
    value.initiatorUserId && (creatorOptions.value.find(option => option.value === value.initiatorUserId)?.title || value.initiatorUserId),
    value.search].filter(Boolean).join(' · ') || t('taskHistory.allFilters')
})
let debounce: ReturnType<typeof setTimeout> | undefined
let filterController: AbortController | undefined
let filterRevision = 0
watch(searchDraft, value => {
  clearTimeout(debounce);
  debounce = setTimeout(() => {
    search.value = value?.trim() ?? ''
  }, 300)
})
watch(() => route.query.workerId, value => {
  worker.value = typeof value === 'string' ? value : 'all'
})
watch(() => route.query.state, value => {
  state.value = stateFromQuery(value)
})
watch(() => [props.scope, auth.user?.id, auth.user?.role, auth.user?.status], async () => {
  const revision = ++filterRevision
  filterController?.abort()
  filterData.value = {initiators: [], workers: []}
  filterError.value = ''
  if (!administration.value || !auth.isAdmin) {
    return
  }
  const controller = new AbortController()
  filterController = controller
  try {
    const value = await TaskHistoryAPI.filters(controller.signal);
    if (revision === filterRevision) {
      filterData.value = value
    }
  } catch (value) {
    if (revision === filterRevision && !controller.signal.aborted) {
      filterError.value = value instanceof Error ? value.message : t('components.common.operationFailed')
    }
  }
}, {immediate: true})

function closeClear(): void {
  clearPreview.value = undefined;
  clearFilter.value = undefined
}

onScopeDispose(() => {
  clearTimeout(debounce);
  filterRevision++;
  filterController?.abort()
})
</script>

<style scoped>
.task-board {
  min-width: 0;
}

.task-filters {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  padding: 0 0 8px;
}

.task-filters > * {
  flex: 1 1 150px;
  min-width: 0;
  max-width: 240px;
}

.task-filters > :first-child {
  flex: 2 1 230px;
  max-width: none;
}

.task-count {
  padding: 0 0 12px;
}

.task-empty {
  padding: 44px 16px;
  text-align: center;
}

.task-pagination {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 12px;
  padding: 12px 16px;
  border-top: 1px solid rgba(var(--v-theme-on-surface), .07);
}

@media (max-width: 600px) {
  .task-filters > * {
    max-width: none;
    flex-basis: 130px;
  }

  .task-pagination {
    justify-content: space-between;
    gap: 4px;
  }
}
</style>
