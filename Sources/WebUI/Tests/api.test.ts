import { AuthAPI } from '../src/apis/AuthAPI'
import { MediaAPI } from '../src/apis/MediaAPI'
import { TaskHistoryAPI } from '../src/apis/TaskHistoryAPI'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, post, query, request, setCsrfToken } from '../src/apis/APIBase'
import { i18n } from '../src/plugins/i18n'

beforeEach(() =>
{
  i18n.global.locale.value = 'zh-CN'
  vi.stubGlobal('window', { location: { origin: 'http://127.0.0.1:5784' } })
  vi.stubEnv('DEV', true)
  vi.stubEnv('VITE_API_BASE_URL', 'http://127.0.0.1:4784')
})

afterEach(() =>
{
  vi.unstubAllEnvs()
  vi.unstubAllGlobals()
  setCsrfToken('')
})

describe('API 请求', () =>
{
  it.each(['zh-CN', 'en-US', 'ja-JP'] as const)('请求携带当前界面语言 %s', async language =>
  {
    i18n.global.locale.value = language
    const fetcher = vi.fn().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetcher)
    await request('auth/me')
    expect(new Headers(fetcher.mock.calls[0]![1].headers).get('Accept-Language')).toBe(language)
  })

  it('连接失败使用当前语言的操作提示', async () =>
  {
    i18n.global.locale.value = 'en-US'
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('network failed')))
    await expect(request('sessions')).rejects.toThrow('Cannot connect to the server')
  })

  it('使用 Cookie 和 CSRF 请求头提交修改，保留 raw JSON', async () =>
  {
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({ id: 'clip-1' }), { headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetcher)
    setCsrfToken('csrf-test')
    expect(await post('jobs', { kind: 'Clip' })).toEqual({ id: 'clip-1' })
    const [path, options] = fetcher.mock.calls[0]!
    expect(path).toBe('http://127.0.0.1:4784/api/jobs')
    expect(options.credentials).toBe('include')
    expect((options.headers as Headers).get('X-CSRF-TOKEN')).toBe('csrf-test')
    expect(JSON.parse(options.body)).toEqual({ kind: 'Clip' })
  })

  it('发布环境使用当前站点，已有 API 前缀不重复添加', async () =>
  {
    vi.stubEnv('DEV', false)
    const fetcher = vi.fn().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetcher)
    await request('/api/auth/me')
    expect(fetcher.mock.calls[0]![0]).toBe('http://127.0.0.1:5784/api/auth/me')
  })

  it('读取不携带修改请求的 CSRF 头，正确处理空响应', async () =>
  {
    const fetcher = vi.fn().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetcher)
    setCsrfToken('csrf-test')
    expect(await request('auth/me')).toBeUndefined()
    expect((fetcher.mock.calls[0]![1].headers as Headers).has('X-CSRF-TOKEN')).toBe(false)
  })

  it('放弃录像补传提交无请求体并接受没有内容长度的 202 空响应', async () =>
  {
    const fetcher = vi.fn().mockResolvedValue(new Response(null, { status: 202 }))
    vi.stubGlobal('fetch', fetcher)
    setCsrfToken('csrf-test')

    await expect(TaskHistoryAPI.abandonRecordingUpload('capture/id')).resolves.toBeUndefined()

    const [path, options] = fetcher.mock.calls[0]!
    expect(new URL(path).pathname).toBe('/api/admin/worker-tasks/recording/capture%2Fid/abandon-upload')
    expect(options).toMatchObject({ method: 'POST', credentials: 'include' })
    expect(options.body).toBeUndefined()
    expect((options.headers as Headers).get('X-CSRF-TOKEN')).toBe('csrf-test')
  })

  it.each([
    [{ detail: '切片已过期' }, '切片已过期'],
    [{ code: 'pending', message: '等待管理员审核' }, '等待管理员审核'],
    [{ errors: { email: ['邮箱格式错误'] } }, '邮箱格式错误'],
  ])('兼容后端错误结构 %j', async (body, message) =>
  {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(body), { status: 403 })))
    await expect(request('clips')).rejects.toMatchObject({ name: 'ApiError', status: 403, message })
  })

  it('保留取消信号，不把主动取消显示为网络故障', async () =>
  {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new DOMException('aborted', 'AbortError')))
    await expect(request('sessions')).rejects.toMatchObject({ name: 'AbortError' })
  })

  it('网络故障返回可展示错误，参数过滤不丢失 0 和 false', async () =>
  {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('network failed')))
    await expect(request('sessions')).rejects.toBeInstanceOf(ApiError)
    expect(query({ fromMs: 0, enabled: false, kind: undefined, search: '' })).toBe('?fromMs=0&enabled=false')
  })
})


describe('直播间搜索 API', () =>
{
  it('编码关键词、页码和账号，并传递取消信号', async () =>
  {
    const controller = new AbortController()
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({ results: [], issues: [], page: 2, hasMore: false }), { headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetcher)
    await AuthAPI.searchBilibiliRooms({ keyword: '主播 & 名字', page: 2, accountId: 'account-1' }, controller.signal)
    const [path, options] = fetcher.mock.calls[0]!
    const url = new URL(path)
    expect(url.pathname).toBe('/api/admin/bilibili/rooms/search')
    expect(url.searchParams.get('keyword')).toBe('主播 & 名字')
    expect(url.searchParams.get('page')).toBe('2')
    expect(url.searchParams.get('accountId')).toBe('account-1')
    expect(options.signal).toBe(controller.signal)
    expect(options.body).toBeUndefined()
  })
})

describe('播放窗口 API', () =>
{
  it('预取携带取消信号和请求标记，并接受 204 跳过', async () =>
  {
    const controller = new AbortController()
    const fetcher = vi.fn().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetcher)
    expect(await MediaAPI.playback('session/a', 30000, { signal: controller.signal, prefetch: true })).toBeUndefined()
    const [path, options] = fetcher.mock.calls[0]!
    expect(new URL(path).pathname).toBe('/api/sessions/session%2Fa/playback')
    expect(options.method).toBe('POST')
    expect(options.signal).toBe(controller.signal)
    expect(JSON.parse(options.body)).toEqual({ offsetMs: 30000, prefetch: true })
  })
})
