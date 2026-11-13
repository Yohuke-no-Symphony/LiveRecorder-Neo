import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MediaAPI } from '../src/apis/MediaAPI'
import { setCsrfToken } from '../src/apis/APIBase'

beforeEach(() =>
{
  vi.stubGlobal('window', { location: { origin: 'http://127.0.0.1:5784' } })
  vi.stubEnv('DEV', false)
  setCsrfToken('session-trash-token')
})
afterEach(() =>
{
  vi.unstubAllEnvs()
  vi.unstubAllGlobals()
  setCsrfToken('')
})

describe('录播删除API', () =>
{
  it('单场删除编码ID，使用DELETE和CSRF并接受204', async () =>
  {
    const fetcher = vi.fn().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetcher)
    await MediaAPI.deleteSession('session /&1')
    const [url, options] = fetcher.mock.calls[0]!
    expect(url).toBe('http://127.0.0.1:5784/api/sessions/session%20%2F%261')
    expect(options.method).toBe('DELETE')
    expect(new Headers(options.headers).get('X-CSRF-TOKEN')).toBe('session-trash-token')
  })

  it('批量请求提交逐项结果所对应的场次ID并保留失败原因', async () =>
  {
    const results = [{ sessionId: 'a', success: true }, { sessionId: 'b', success: false, error: '录制中' }]
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify(results), { headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetcher)
    expect(await MediaAPI.trashSessions(['a', 'b'])).toEqual(results)
    const [url, options] = fetcher.mock.calls[0]!
    expect(url).toBe('http://127.0.0.1:5784/api/sessions/trash')
    expect(options.method).toBe('POST')
    expect(JSON.parse(options.body)).toEqual({ sessionIds: ['a', 'b'] })
    expect(new Headers(options.headers).get('X-CSRF-TOKEN')).toBe('session-trash-token')
  })
})
