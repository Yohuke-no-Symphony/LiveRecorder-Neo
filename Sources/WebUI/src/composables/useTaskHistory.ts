import { computed, onScopeDispose, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { TaskHistoryAPI } from '@/apis/TaskHistoryAPI'
import { MediaAPI } from '@/apis/MediaAPI'
import { AiAnalysisAPI } from '@/apis/AiAnalysisAPI'
import { TranscodingAPI } from '@/apis/TranscodingAPI'
import { useAuthStore } from '@/stores/auth'
import type { TaskClearPreview, TaskQuery, TaskScope, TaskSummary } from '@/models/taskHistory'
import { taskKey } from '@/utilities/taskHistory'
import { usePolling } from './usePolling'

export function useTaskHistory(scope: () => TaskScope, filters: () => TaskQuery)
{
  const auth = useAuthStore()
  const { t } = useI18n()
  const items = ref<TaskSummary[]>([])
  const total = ref(0)
  const loading = ref(false)
  const error = ref('')
  const actionError = ref('')
  const notice = ref('')
  const actionKey = ref('')
  const nextCursor = ref<string | null>(null)
  const cursors = ref<string[]>([])
  const selectedTask = ref<TaskSummary>()
  const selectedStageId = ref('')
  const deleteTarget = ref<TaskSummary>()
  const interruptTarget = ref<TaskSummary>()
  const abandonUploadTarget = ref<TaskSummary>()
  const clearPreview = ref<TaskClearPreview>()
  const clearFilter = ref<TaskQuery>()
  const page = computed(() => cursors.value.length + 1)
  const mayRead = computed(() => auth.hasAccess && (scope() !== 'administration' || auth.isAdmin))
  const identity = computed(() => JSON.stringify([auth.user?.id, auth.user?.role, auth.user?.status, [...(auth.user?.permissions ?? [])].sort(), scope()]))
  let disposed = false
  let contextRevision = 0
  let requestRevision = 0
  let controller: AbortController | undefined

  const message = (value: unknown) => value instanceof Error ? value.message : t('components.common.operationFailed')

  async function load(): Promise<void>
  {
    const request = ++requestRevision
    const context = contextRevision
    controller?.abort()
    if (!mayRead.value || disposed) { return }
    controller = new AbortController()
    loading.value = true
    const current = () => !disposed && context === contextRevision && request === requestRevision
    try
    {
      const result = await TaskHistoryAPI.list(scope(), { ...filters(), cursor: cursors.value.at(-1) }, controller.signal)
      if (!current()) { return }
      items.value = result.items
      total.value = result.total
      nextCursor.value = result.nextCursor
      error.value = ''
      if (selectedTask.value)
      {
        const target = selectedTask.value
        const selected = result.items.find(item => taskKey(item) === taskKey(target))
          ?? await TaskHistoryAPI.detail(scope(), target, controller.signal)
        if (current() && selectedTask.value && taskKey(selectedTask.value) === taskKey(target)) { selectedTask.value = selected }
      }
    }
    catch (value)
    {
      if (current()) { error.value = message(value) }
    }
    finally
    {
      if (current()) { loading.value = false }
    }
  }

  async function refresh(): Promise<void>
  {
    notice.value = ''
    await load()
  }

  async function perform(key: string, operation: () => Promise<unknown>): Promise<boolean>
  {
    if (actionKey.value || !mayRead.value || disposed) { return false }
    const context = contextRevision
    actionKey.value = key
    actionError.value = ''
    try
    {
      await operation()
      if (disposed || context !== contextRevision) { return false }
      await load()
      if (disposed || context !== contextRevision) { return false }
      if (!items.value.length && cursors.value.length)
      {
        cursors.value.pop()
        await load()
        if (disposed || context !== contextRevision) { return false }
      }
      return true
    }
    catch (value)
    {
      if (!disposed && context === contextRevision) { actionError.value = message(value) }
      return false
    }
    finally
    {
      if (!disposed && context === contextRevision) { actionKey.value = '' }
    }
  }

  async function act(task: TaskSummary, action: 'cancel' | 'retry'): Promise<void>
  {
    if (!(action === 'cancel' ? task.canCancel : task.canRetry)) { return }
    await perform(`${taskKey(task)}:${action}`, async () =>
    {
      if (task.kind === 'transcode')
      {
        if (action === 'cancel') { await TranscodingAPI.cancelJob(task.id) }
        else { await TranscodingAPI.retryJob(task.id) }
      }
      else if (task.kind === 'ai') { await AiAnalysisAPI.cancel(task.id) }
      else if (task.kind === 'media')
      {
        if (action === 'cancel') { await MediaAPI.cancelJob(task.id) }
        else { await MediaAPI.retryJob(task.id) }
      }
    })
  }

  async function dismiss(): Promise<void>
  {
    const task = deleteTarget.value
    if (!task?.canDelete) { return }
    if (await perform(`${taskKey(task)}:delete`, () => TaskHistoryAPI.dismiss(scope(), task)))
    {
      deleteTarget.value = undefined
    }
  }

  function openRecordingInterruption(task: TaskSummary): void
  {
    if (actionKey.value || scope() !== 'administration' || !mayRead.value || !task.canInterruptRecording) { return }
    actionError.value = ''
    interruptTarget.value = task
  }

  function closeRecordingInterruption(): void
  {
    if (actionKey.value) { return }
    interruptTarget.value = undefined
    actionError.value = ''
  }

  async function interruptRecording(): Promise<void>
  {
    const task = interruptTarget.value
    const reportedAt = task?.lastUpdatedAtUtc
    if (!task?.canInterruptRecording || !reportedAt || scope() !== 'administration') { return }
    if (await perform(`${taskKey(task)}:interrupt`, () => TaskHistoryAPI.interruptRecording(task.id, reportedAt)))
    {
      interruptTarget.value = undefined
    }
  }

  function openRecordingUploadAbandonment(task: TaskSummary): void
  {
    if (actionKey.value || scope() !== 'administration' || !mayRead.value || task.kind !== 'recording'
      || !task.canAbandonRecordingUpload || task.recordingUploadAbandonmentState) { return }
    actionError.value = ''
    abandonUploadTarget.value = task
  }

  function closeRecordingUploadAbandonment(): void
  {
    if (actionKey.value) { return }
    abandonUploadTarget.value = undefined
    actionError.value = ''
  }

  async function abandonRecordingUpload(): Promise<void>
  {
    const task = abandonUploadTarget.value
    if (!task?.canAbandonRecordingUpload || scope() !== 'administration') { return }
    if (await perform(`${taskKey(task)}:abandon-upload`, () => TaskHistoryAPI.abandonRecordingUpload(task.id)))
    {
      abandonUploadTarget.value = undefined
    }
  }

  async function previewClear(): Promise<void>
  {
    if (actionKey.value || !mayRead.value) { return }
    const context = contextRevision
    actionKey.value = 'preview'
    actionError.value = ''
    notice.value = ''
    try
    {
      const filter = { ...filters(), cursor: undefined }
      const preview = await TaskHistoryAPI.previewClear(scope(), filter)
      if (!disposed && context === contextRevision)
      {
        if (preview.count > 0)
        {
          clearPreview.value = preview
          clearFilter.value = filter
        }
        else { notice.value = t('taskHistory.noCompleted') }
      }
    }
    catch (value)
    {
      if (!disposed && context === contextRevision) { actionError.value = message(value) }
    }
    finally
    {
      if (!disposed && context === contextRevision) { actionKey.value = '' }
    }
  }

  async function clearCompleted(): Promise<void>
  {
    if (!clearPreview.value || !clearFilter.value) { return }
    const filter = clearFilter.value
    const cutoff = clearPreview.value.completedBeforeUtc
    if (await perform('clear', () => TaskHistoryAPI.clearCompleted(scope(), filter, cutoff)))
    {
      clearPreview.value = undefined
      clearFilter.value = undefined
    }
  }

  function openStage(task: TaskSummary, id: string): void
  {
    selectedTask.value = task
    selectedStageId.value = id
  }

  function nextPage(): void
  {
    if (nextCursor.value && !loading.value)
    {
      cursors.value.push(nextCursor.value)
      void load()
    }
  }

  function previousPage(): void
  {
    if (cursors.value.length && !loading.value)
    {
      cursors.value.pop()
      void load()
    }
  }

  watch(() => JSON.stringify(filters()), () =>
  {
    cursors.value = []
    nextCursor.value = null
    notice.value = ''
    void load()
  })
  watch(identity, () =>
  {
    contextRevision++
    requestRevision++
    controller?.abort()
    items.value = []
    total.value = 0
    cursors.value = []
    nextCursor.value = null
    selectedTask.value = deleteTarget.value = interruptTarget.value = abandonUploadTarget.value = undefined
    clearPreview.value = undefined
    clearFilter.value = undefined
    actionKey.value = error.value = actionError.value = notice.value = ''
    loading.value = false
    void load()
  }, { flush: 'sync' })
  onScopeDispose(() =>
  {
    disposed = true
    controller?.abort()
  })
  usePolling(load, 4000)
  return { items, total, loading, error, actionError, notice, actionKey, page, nextCursor, cursors,
    selectedTask, selectedStageId, deleteTarget, interruptTarget, abandonUploadTarget, clearPreview, clearFilter,
    refresh, act, dismiss, openRecordingInterruption, closeRecordingInterruption, interruptRecording,
    openRecordingUploadAbandonment, closeRecordingUploadAbandonment, abandonRecordingUpload,
    previewClear, clearCompleted, openStage, nextPage, previousPage }
}
