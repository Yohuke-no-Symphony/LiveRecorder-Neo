import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, post, query, request, setCsrfToken } from '../src/apis/APIBase'

beforeEach(() =>
{
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
