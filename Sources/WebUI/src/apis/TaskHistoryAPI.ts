import { post, query, request, requestResponse } from './APIBase'
import type { TaskClearPreview, TaskFilters, TaskPage, TaskQuery, TaskScope, TaskSummary } from '@/models/taskHistory'
import type { WorkerLogPage } from '@/models/transcoding'

const root = (scope: TaskScope) => scope === 'administration' ? 'admin/worker-tasks' : 'tasks'
const target = (scope: TaskScope, task: Pick<TaskSummary, 'kind' | 'id'>) => `${root(scope)}/${encodeURIComponent(task.kind)}/${encodeURIComponent(task.id)}`

async function active(workerIds: string[], signal?: AbortSignal): Promise<Record<string, TaskSummary[]>>
{
  const batches: Promise<Record<string, TaskSummary[]>>[] = []
  for (let offset = 0; offset < workerIds.length; offset += 256)
  {
    batches.push(request<Record<string, TaskSummary[]>>('admin/worker-tasks/active', {
      method: 'POST', body: JSON.stringify({ workerIds: workerIds.slice(offset, offset + 256) }), signal,
    }))
  }
  return Object.assign({}, ...await Promise.all(batches)) as Record<string, TaskSummary[]>
}

export const TaskHistoryAPI = {
  active,
  list: (scope: TaskScope, filters: TaskQuery, signal?: AbortSignal) => request<TaskPage>(`${root(scope)}${query({ ...filters })}`, { signal }),
  detail: (scope: TaskScope, task: Pick<TaskSummary, 'kind' | 'id'>, signal?: AbortSignal) => request<TaskSummary>(target(scope, task), { signal }),
  logs: (scope: TaskScope, task: Pick<TaskSummary, 'kind' | 'id' | 'attempt'>, stage: string, after?: number, signal?: AbortSignal) => request<WorkerLogPage>(`${target(scope, task)}/logs${query({ stage, attempt: task.attempt, after, take: 200 })}`, { signal }),
  filters: (signal?: AbortSignal) => request<TaskFilters>('admin/worker-tasks/filters', { signal }),
  previewClear: (scope: TaskScope, filters: TaskQuery, signal?: AbortSignal) => request<TaskClearPreview>(`${root(scope)}/clear-completed/preview${query({ ...filters, cursor: undefined })}`, { signal }),
  clearCompleted: (scope: TaskScope, filter: TaskQuery, completedBeforeUtc: string) => post<{ removed: number }>(`${root(scope)}/clear-completed`, { filter: { ...filter, cursor: undefined }, completedBeforeUtc }),
  interruptRecording: (id: string, lastReportedAtUtc: string) => post<void>(`admin/worker-tasks/recording/${encodeURIComponent(id)}/interrupt`, { lastReportedAtUtc }),
  async abandonRecordingUpload(id: string): Promise<void>
  {
    await requestResponse(`admin/worker-tasks/recording/${encodeURIComponent(id)}/abandon-upload`, { method: 'POST' }, 'application/json')
  },
  dismiss: (scope: TaskScope, task: TaskSummary) => request<void>(target(scope, task), { method: 'DELETE', body: JSON.stringify({ attempt: task.attempt, finishedAtUtc: task.finishedAtUtc, state: task.state }) }),
}
