// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import { nextTick } from 'vue'
import type { ServerLogEntry, ServerLogPage, ServerLogResetReason } from '../src/models/serverLogs'
import { i18n } from '../src/plugins/i18n'
import { useAuthStore } from '../src/stores/auth'
import ServerLogsView from '../src/views/ServerLogsView.vue'

const fetchLogs = vi.fn<typeof fetch>()
const timestamp = '2026-11-22T12:34:56Z'
let pinia: Pinia
let wrapper: VueWrapper | undefined

beforeEach(() =>
{
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
  vi.stubGlobal('fetch', fetchLogs)
  fetchLogs.mockReset()
  i18n.global.locale.value = 'zh-CN'
  pinia = createPinia()
  setActivePinia(pinia)
  useAuthStore().user = { id: 'admin', email: 'admin@example.test', displayName: '管理员', role: 'Admin', status: 'Active', permissions: [] }
})

afterEach(() =>
{
  wrapper?.unmount()
  wrapper = undefined
  disposePinia(pinia)
  i18n.global.locale.value = 'zh-CN'
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

function openPage(): VueWrapper
{
  wrapper = mount(ServerLogsView, {
    global: {
      plugins: [pinia, i18n],
      stubs: {
        ContentCard: { template: '<section><slot /></section>' },
        VAlert: { template: '<div role="alert"><slot /></div>' },
        VBtn: { template: '<button type="button"><slot /></button>' },
        VProgressLinear: true,
        VSpacer: true,
        VTextField: {
          props: ['modelValue', 'label'],
          emits: ['update:modelValue'],
          template: '<label>{{ label }}<input type="search" :value="modelValue" @input="$emit(\'update:modelValue\', $event.target.value)" /></label>',
        },
      },
    },
  })
  return wrapper
}

function entry(id: number, message = `record-${id}`): ServerLogEntry
{
  return { id, timestampUtc: timestamp, level: 'Warning', category: 'Yohuke.LiveRecorder.Server', message, exceptionType: 'IOException' }
}

function response(entries: ServerLogEntry[], nextCursor: string, hasMore = false, resetReason: ServerLogResetReason | null = null): Response
{
  return Response.json({ entries, nextCursor, hasMore, resetReason } satisfies ServerLogPage)
}

function delayedResponse()
{
  let resolve!: (value: Response) => void
  const promise = new Promise<Response>(complete => { resolve = complete })
  return { promise, resolve }
}

function requestedCursor(index: number): string | null
{
  const url = new URL(String(fetchLogs.mock.calls[index]![0]), window.location.href)
  expect(url.pathname).toBe('/api/admin/server-logs')
  return url.searchParams.get('cursor')
}

async function advance(milliseconds: number): Promise<void>
{
  await vi.advanceTimersByTimeAsync(milliseconds)
  await flushPromises()
}

describe('管理员服务器日志', () =>
{
  it('使用会话和游标串行补读，并在三种语言下将消息渲染为纯文本', async () =>
  {
    const pending = delayedResponse()
    const firstMessage = '<img src=x onerror=example()>\nplain text'
    fetchLogs.mockResolvedValueOnce(response([entry(1, firstMessage)], 'stream:1'))
      .mockReturnValueOnce(pending.promise)
      .mockResolvedValueOnce(response([entry(3)], 'stream:3'))
    const page = openPage()
    await flushPromises()
    expect(requestedCursor(0)).toBeNull()
    expect(fetchLogs.mock.calls[0]![1]).toMatchObject({ credentials: 'include', cache: 'no-store' })
    expect(page.get('[role="log"] pre').text()).toBe(firstMessage)
    expect(page.find('[role="log"] img').exists()).toBe(false)
    expect(page.get('time').attributes('datetime')).toBe(timestamp)
    expect(page.get('[role="log"]').text()).toContain('Yohuke.LiveRecorder.Server')
    expect(page.get('[role="log"]').text()).toContain('IOException')
    for (const [locale, title, level] of [
      ['zh-CN', '服务器日志', '警告'],
      ['en-US', 'Server logs', 'Warning'],
      ['ja-JP', 'サーバーログ', '警告'],
    ] as const)
    {
      i18n.global.locale.value = locale
      await nextTick()
      expect(page.get('h1').text()).toBe(title)
      expect(page.get('[role="log"]').attributes('aria-label')).toBe(title)
      expect(page.get('[role="log"] .text-warning').text()).toBe(level)
    }

    await advance(2999)
    expect(fetchLogs).toHaveBeenCalledTimes(1)
    await advance(1)
    expect(requestedCursor(1)).toBe('stream:1')
    await advance(6000)
    expect(fetchLogs).toHaveBeenCalledTimes(2)
    pending.resolve(response([entry(2)], 'stream:2', true))
    await flushPromises()
    await advance(250)
    expect(requestedCursor(2)).toBe('stream:2')
    expect(page.findAll('[data-log-id]').map(row => row.attributes('data-log-id'))).toEqual(['1', '2', '3'])
  })

  it('断线保留日志与游标，重启及覆盖时更新列表和提示', async () =>
  {
    fetchLogs.mockResolvedValueOnce(response([entry(1, 'old process')], 'old:1'))
      .mockRejectedValueOnce(new TypeError('offline'))
      .mockResolvedValueOnce(response([entry(1, 'new process')], 'new:1', false, 'processRestarted'))
      .mockResolvedValueOnce(response([entry(1500)], 'new:1500', false, 'bufferOverwritten'))
    const page = openPage()
    await flushPromises()

    await advance(3000)
    expect(page.text()).toContain('正在重试')
    expect(page.get('[role="log"]').text()).toContain('old process')
    await advance(3000)
    expect(requestedCursor(2)).toBe('old:1')
    expect(page.text()).toContain('服务器已重启')
    expect(page.findAll('[role="log"] pre').map(row => row.text())).toEqual(['new process'])
    await advance(3000)
    expect(requestedCursor(3)).toBe('new:1')
    expect(page.text()).toContain('部分日志已被覆盖')
    expect(page.findAll('[role="log"] pre').map(row => row.text())).toEqual(['record-1500'])
  })

  it('搜索已加载及新增日志的正文、来源、级别和异常类型，清空后恢复完整列表', async () =>
  {
    const warning = entry(1, 'Bilibili refresh paused')
    const information = { ...entry(2, 'Application started'), level: 'Information', category: 'Microsoft.Hosting.Lifetime', exceptionType: null }
    fetchLogs.mockResolvedValueOnce(response([warning, information], 'stream:2'))
      .mockResolvedValueOnce(response([entry(3, 'Bilibili refresh resumed')], 'stream:3'))
    const page = openPage()
    await flushPromises()
    const search = page.get('input[type="search"]')
    const visibleIds = () => page.findAll('[data-log-id]').map(row => row.attributes('data-log-id'))
    expect(page.get('label').text()).toBe('搜索已加载的日志')
    expect(page.get('.log-entry-list').classes()).toContain('log-entry-list-fill')
    await search.setValue('missing')
    expect(visibleIds()).toEqual([])
    expect(page.get('[role="log"]').text()).toContain('暂无匹配的日志')
    await search.setValue('  biliBILI  ')
    expect(visibleIds()).toEqual(['1'])
    expect(fetchLogs).toHaveBeenCalledTimes(1)
    await advance(3000)
    expect(visibleIds()).toEqual(['1', '3'])
    await search.setValue('Hosting.Lifetime')
    expect(visibleIds()).toEqual(['2'])
    await search.setValue('ioexception')
    expect(visibleIds()).toEqual(['1', '3'])
    await search.setValue('警告')
    expect(visibleIds()).toEqual(['1', '3'])
    await search.setValue('')
    expect(visibleIds()).toEqual(['1', '2', '3'])
  })

  it.each([401, 403])('会话返回 %i 时清空日志并停止轮询', async status =>
  {
    fetchLogs.mockResolvedValueOnce(response([entry(1)], 'stream:1'))
      .mockResolvedValueOnce(Response.json({}, { status }))
    const page = openPage()
    await flushPromises()
    expect(page.findAll('[data-log-id]')).toHaveLength(1)
    await advance(3000)
    expect(page.findAll('[data-log-id]')).toHaveLength(0)
    expect(page.text()).toContain('当前会话无权查看服务器日志')
    await advance(9000)
    expect(fetchLogs).toHaveBeenCalledTimes(2)
  })

  it('普通账号不查询，角色变化和离页会取消请求并丢弃迟到的日志', async () =>
  {
    const auth = useAuthStore()
    auth.user!.role = 'User'
    const oldRequest = delayedResponse()
    const lastRequest = delayedResponse()
    fetchLogs.mockReturnValueOnce(oldRequest.promise)
      .mockResolvedValueOnce(response([entry(2)], 'current:2'))
      .mockReturnValueOnce(lastRequest.promise)
    const page = openPage()
    expect(fetchLogs).not.toHaveBeenCalled()

    auth.user!.role = 'Admin'
    const oldSignal = fetchLogs.mock.calls[0]![1]?.signal
    auth.user!.role = 'User'
    expect(oldSignal?.aborted).toBe(true)
    auth.user!.role = 'Admin'
    oldRequest.resolve(response([entry(1)], 'previous:1'))
    await flushPromises()
    expect(page.findAll('[data-log-id]').map(row => row.attributes('data-log-id'))).toEqual(['2'])
    await advance(3000)
    const lastSignal = fetchLogs.mock.calls[2]![1]?.signal
    page.unmount()
    wrapper = undefined
    expect(lastSignal?.aborted).toBe(true)
    lastRequest.resolve(response([entry(3)], 'current:3'))
    await flushPromises()
    await advance(9000)
    expect(fetchLogs).toHaveBeenCalledTimes(3)
  })

  it('向上滚动时保留位置，回到最新后继续跟随新增日志', async () =>
  {
    fetchLogs.mockResolvedValueOnce(response([entry(1), entry(2), entry(3), entry(4)], 'stream:4'))
      .mockResolvedValueOnce(response([entry(5)], 'stream:5'))
      .mockResolvedValueOnce(response([entry(6)], 'stream:6'))
    const page = openPage()
    await flushPromises()
    const log = page.get<HTMLElement>('[role="log"]')
    const rows = () => log.findAll('[data-log-id]').map(row => row.element)
    Object.defineProperties(log.element, {
      clientHeight: { value: 80 },
      scrollHeight: { get: () => rows().length * 40 },
    })
    for (const row of rows())
    {
      row.getBoundingClientRect = () => new DOMRect(0, rows().indexOf(row) * 40 - log.element.scrollTop, 300, 40)
    }
    log.element.scrollTop = 38
    await log.trigger('scroll')
    await advance(3000)
    expect(log.element.scrollTop).toBe(38)
    expect(page.get('button').text()).toBe('返回最新')
    await page.get('button').trigger('click')
    expect(log.element.scrollTop).toBe(200)
    await advance(3000)
    expect(log.element.scrollTop).toBe(240)
    expect(page.find('button').exists()).toBe(false)
  })

  it('列表只保留最近一千条，淘汰旧日志时以当前阅读位置为锚点', async () =>
  {
    for (let batch = 0; batch < 7; batch++)
    {
      const logs = Array.from({ length: 200 }, (_, index) => entry(batch * 200 + index + 1))
      fetchLogs.mockResolvedValueOnce(response(logs, `stream:${logs.at(-1)!.id}`, batch > 0 && batch < 4))
    }
    const page = openPage()
    await flushPromises()
    await advance(3000)
    await advance(750)
    const log = page.get<HTMLElement>('[role="log"]')
    const rows = () => log.findAll('[data-log-id]').map(row => row.element)
    Object.defineProperties(log.element, {
      clientHeight: { value: 80 },
      scrollHeight: { get: () => rows().length * 40 },
    })
    const anchor = log.get('[data-log-id="206"]').element
    anchor.getBoundingClientRect = () => new DOMRect(0, rows().indexOf(anchor) * 40 - log.element.scrollTop, 300, 40)
    log.element.scrollTop = 8210
    await log.trigger('scroll')

    await advance(3000)
    expect(rows()).toHaveLength(1000)
    expect(rows()[0]?.getAttribute('data-log-id')).toBe('201')
    expect(log.element.scrollTop).toBe(210)
    await advance(3000)
    expect(rows()).toHaveLength(1000)
    expect(rows()[0]?.getAttribute('data-log-id')).toBe('401')
    expect(log.element.scrollTop).toBe(0)
  })
})
