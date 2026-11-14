// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, ref } from 'vue'
import { ApiError } from '../src/apis/APIBase'
import { useAiAnalysis } from '../src/composables/useAiAnalysis'
import type { AiAnalysisJob, AiAnalysisStatus } from '../src/models/aiAnalysis'
import { i18n } from '../src/plugins/i18n'

const { status, generate, cancel, liveMarkerStatus, generateLiveMarkers } = vi.hoisted(() => ({ status: vi.fn(), generate: vi.fn(), cancel: vi.fn(), liveMarkerStatus: vi.fn(), generateLiveMarkers: vi.fn() }))
vi.mock('../src/apis/AiAnalysisAPI', () => ({ AiAnalysisAPI: { status, generate, cancel, liveMarkerStatus, generateLiveMarkers } }))
const mounted: VueWrapper[] = []

function job(state: AiAnalysisJob['state'] = 'Running'): AiAnalysisJob
{
  return { id: 'job', sessionId: 's1', title: '录像标题', cancelRequested: false, state, progress: 25, promptTokens: 10, completionTokens: 5, estimatedTokens: 20, tokenBudget: 10000, modelRequests: 1, toolCalls: 2, errorCode: null, createdAtUtc: '', finishedAtUtc: null }
}
function response(currentJob: AiAnalysisJob | null = null): AiAnalysisStatus
{
  return { enabled: true, canGenerate: true, currentJob, latestCompleted: null, latestPartial: null, stale: false }
}
function deferred<T>()
{
  let resolve!: (value: T) => void
  let reject!: (reason: Error) => void
  const promise = new Promise<T>((accept, fail) => { resolve = accept; reject = fail })
  return { promise, resolve, reject }
}
async function setup(session = 's1', ended = true)
{
  const sessionId = ref(session)
  const hasEnded = ref(ended)
  let model!: ReturnType<typeof useAiAnalysis>
  const component = defineComponent({ setup: () => { model = useAiAnalysis(sessionId, hasEnded); return () => null } })
  const wrapper = mount(component, { global: { plugins: [i18n] } })
  mounted.push(wrapper)
  await flushPromises()
  return { model, sessionId, hasEnded, wrapper }
}
beforeEach(() => { vi.resetAllMocks(); status.mockResolvedValue(response()); i18n.global.locale.value = 'zh-CN' })
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); vi.useRealTimers() })

describe('页面共享 AI 分析状态', () =>
{
  it('话题分析与直播记录使用独立状态和生成接口，进入页面只读取', async () =>
  {
    liveMarkerStatus.mockResolvedValue(response({ ...job('Completed'), purpose: 'LiveMarkers', liveMarkerFileId: 'old-file' }))
    generateLiveMarkers.mockResolvedValue({ ...job('Queued'), purpose: 'LiveMarkers' })
    let analysis!: ReturnType<typeof useAiAnalysis>
    let markers!: ReturnType<typeof useAiAnalysis>
    const wrapper = mount(defineComponent({ setup: () =>
    {
      analysis = useAiAnalysis('s1', true)
      markers = useAiAnalysis('s1', true, { purpose: 'LiveMarkers' })
      return () => null
    } }), { global: { plugins: [i18n] } })
    mounted.push(wrapper)
    await flushPromises()
    expect(status).toHaveBeenCalledTimes(1)
    expect(liveMarkerStatus).toHaveBeenCalledTimes(1)
    expect(generateLiveMarkers).not.toHaveBeenCalled()
    expect(generate).not.toHaveBeenCalled()
    await markers.generate()
    expect(generateLiveMarkers).toHaveBeenCalledWith('s1', true)
    expect(analysis.status.value?.currentJob).toBeNull()
    expect(generate).not.toHaveBeenCalled()
  })

  it('运行中每 3 秒刷新，完成后每 30 秒刷新', async () =>
  {
    vi.useFakeTimers()
    status.mockResolvedValueOnce(response(job('Queued'))).mockResolvedValue(response(job('Completed')))
    const { model } = await setup()
    expect(model.running.value).toBe(true)
    await vi.advanceTimersByTimeAsync(2999)
    expect(status).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1)
    expect(status).toHaveBeenCalledTimes(2)
    expect(model.running.value).toBe(false)
    await vi.advanceTimersByTimeAsync(29999)
    expect(status).toHaveBeenCalledTimes(2)
    await vi.advanceTimersByTimeAsync(1)
    expect(status).toHaveBeenCalledTimes(3)
  })

  it('等待状态响应时不叠加轮询，离开页面时取消读取与定时器', async () =>
  {
    vi.useFakeTimers()
    const pending = deferred<AiAnalysisStatus>()
    status.mockReturnValueOnce(pending.promise)
    const { model, wrapper } = await setup()
    const signal = status.mock.calls[0]![1] as AbortSignal
    await vi.advanceTimersByTimeAsync(90000)
    expect(status).toHaveBeenCalledTimes(1)
    expect(model.loading.value).toBe(true)
    wrapper.unmount()
    expect(signal.aborted).toBe(true)
    pending.resolve(response(job()))
    await flushPromises()
    await vi.advanceTimersByTimeAsync(90000)
    expect(status).toHaveBeenCalledTimes(1)
    expect(model.status.value).toBeUndefined()
  })

  it('A → B → A 时丢弃第一次 A 的迟到响应', async () =>
  {
    const pending = deferred<AiAnalysisStatus>()
    status.mockReturnValueOnce(pending.promise)
    const { model, sessionId } = await setup('A')
    const firstSignal = status.mock.calls[0]![1] as AbortSignal
    sessionId.value = 'B'
    await flushPromises()
    sessionId.value = 'A'
    await flushPromises()
    pending.resolve(response(job('Failed')))
    await flushPromises()
    expect(firstSignal.aborted).toBe(true)
    expect(model.status.value?.currentJob).toBeNull()
    expect(model.error.value).toBe('')
  })

  it('迟到的生成操作不能刷新新页面或清除新生成操作的忙碌状态', async () =>
  {
    const first = deferred<AiAnalysisJob>()
    const second = deferred<AiAnalysisJob>()
    generate.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise)
    const { model, sessionId } = await setup('A')
    const oldOperation = model.generate()
    sessionId.value = 'B'
    await flushPromises()
    sessionId.value = 'A'
    await flushPromises()
    const currentOperation = model.generate()
    const reads = status.mock.calls.length
    first.resolve(job('Queued'))
    await oldOperation
    expect(model.busy.value).toBe(true)
    expect(model.showPartial.value).toBe(false)
    expect(status).toHaveBeenCalledTimes(reads)
    status.mockResolvedValue(response(job('Queued')))
    second.resolve(job('Queued'))
    await currentOperation
    await flushPromises()
    expect(model.busy.value).toBe(false)
    expect(model.running.value).toBe(true)
    expect(model.showPartial.value).toBe(true)
    expect(generate.mock.calls).toEqual([['A', false], ['A', false]])
  })

  it('切换录像后丢弃旧取消操作的失败', async () =>
  {
    status.mockResolvedValueOnce(response(job()))
    const pending = deferred<void>()
    cancel.mockReturnValueOnce(pending.promise)
    const { model, sessionId } = await setup()
    const operation = model.cancel()
    expect(cancel).toHaveBeenCalledWith('job')
    sessionId.value = 'new'
    await flushPromises()
    pending.reject(new Error('旧录像取消失败'))
    await operation
    expect(model.error.value).toBe('')
    expect(model.busy.value).toBe(false)
  })

  it('普通查看权限、未结束录像和运行中任务不能生成或越权取消', async () =>
  {
    status.mockResolvedValueOnce({ ...response(job()), canGenerate: false })
    const { model, hasEnded } = await setup()
    await model.generate()
    await model.cancel()
    expect(generate).not.toHaveBeenCalled()
    expect(cancel).not.toHaveBeenCalled()
    status.mockResolvedValue(response())
    await model.refresh()
    hasEnded.value = false
    await model.generate()
    expect(generate).not.toHaveBeenCalled()
    hasEnded.value = true
    status.mockResolvedValue(response(job()))
    await model.refresh()
    await model.generate()
    expect(generate).not.toHaveBeenCalled()
  })

  it('使用当前结果生成重试请求，并按显示的完整或部分报告计算过期状态', async () =>
  {
    const complete = { jobId: 'old', sessionId: 's1', sourceFingerprint: 'old', model: 'test', createdAtUtc: '', completedAtUtc: '', isComplete: true, overview: '完整结果', topics: [] }
    const partial = { ...complete, jobId: 'new', isComplete: false, overview: '部分结果' }
    const data = { ...response(), latestCompleted: complete, latestPartial: partial, stale: true, latestCompletedStale: true, latestPartialStale: false }
    status.mockResolvedValue(data)
    generate.mockResolvedValue(job('Queued'))
    const { model } = await setup()
    expect(model.result.value?.overview).toBe('完整结果')
    expect(model.resultStale.value).toBe(true)
    model.showPartial.value = true
    expect(model.result.value?.overview).toBe('部分结果')
    expect(model.resultStale.value).toBe(false)
    await model.generate()
    expect(generate).toHaveBeenCalledWith('s1', true)
  })

  it('读取权限失效后清除之前加载的数据并禁用生成', async () =>
  {
    status.mockResolvedValueOnce(response(job('Completed')))
    const { model } = await setup()
    status.mockRejectedValueOnce(new ApiError(403, '没有查看权限'))
    await model.refresh()
    expect(model.status.value).toBeUndefined()
    expect(model.canStart.value).toBe(false)
    expect(model.error.value).toBe('没有查看权限')
  })
})
