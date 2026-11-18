import { i18n } from '@/plugins/i18n'
import type { TaskMetric, TaskStage, TaskSummary } from '@/models/taskHistory'
import { formatBytes, formatDate, formatDuration, formatNumber } from './format'

export const taskKey = (task: Pick<TaskSummary, 'kind' | 'id'>) => `${task.kind}:${task.id}`

export function taskStageProgress(stage: TaskStage): string
{
  return typeof stage.progress === 'number' && Number.isFinite(stage.progress)
    ? `${formatNumber(Math.min(100, Math.max(0, stage.progress)), { maximumFractionDigits: 1 })}%` : '—'
}

export function taskStageLabel(id: string, taskType?: string): string
{
  if (id === 'Encoding' && taskType === 'Clip') { return i18n.global.t('taskHistory.stages.Exporting') }
  const key = `taskHistory.stages.${id}`
  return i18n.global.te(key) ? i18n.global.t(key) : id
}

export function taskStageError(stage: TaskStage, taskError?: string | null): string
{
  if (['Completed', 'Succeeded', 'Skipped', 'Pending', 'Queued'].includes(stage.state)) { return '' }
  return stage.error || (['Failed', 'Interrupted', 'Partial'].includes(stage.state) ? taskError : '') || ''
}

export function taskErrorLabel(error: string): string
{
  const key = `taskHistory.errors.${error}`
  return i18n.global.te(key) ? i18n.global.t(key) : error
}

export function taskTypeLabel(type: string): string
{
  const key = `taskHistory.types.${type}`
  return i18n.global.te(key) ? i18n.global.t(key) : type
}

export function taskMetricValue(metric: TaskMetric): string
{
  if (metric.text !== undefined && metric.text !== null)
  {
    if (metric.key === 'currentFile' && i18n.global.te(`taskHistory.artifacts.${metric.text}`))
    {
      return i18n.global.t(`taskHistory.artifacts.${metric.text}`)
    }
    return metric.unit === 'date' ? formatDate(metric.text) : metric.text
  }
  if (typeof metric.value !== 'number' || !Number.isFinite(metric.value)) { return '—' }
  switch (metric.unit)
  {
    case 'bytes': return formatBytes(metric.value)
    case 'bytesPerSecond': return `${formatBytes(metric.value)}/s`
    case 'milliseconds': return formatDuration(metric.value, true)
    case 'fps': return `${formatNumber(metric.value, { maximumFractionDigits: 1 })} FPS`
    case 'speed': return `${formatNumber(metric.value, { maximumFractionDigits: 2 })}×`
    default: return formatNumber(metric.value, { maximumFractionDigits: 1 })
  }
}
