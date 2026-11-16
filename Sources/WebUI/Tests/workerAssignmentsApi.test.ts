import { afterEach, expect, it, vi } from 'vitest'
import { TranscodingAPI } from '../src/apis/TranscodingAPI'

afterEach(() =>
{
  vi.unstubAllGlobals()
  vi.unstubAllEnvs()
})

it('按编码后的 Worker ID 读取分配情况，并传递取消信号', async () =>
{
  vi.stubGlobal('window', { location: { origin: 'http://127.0.0.1:5784' } })
  vi.stubEnv('DEV', false)
  const rooms = [{ targetId: 'target-1', roomId: 123456, name: '测试主播' }]
  const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify(rooms), { headers: { 'Content-Type': 'application/json' } }))
  vi.stubGlobal('fetch', fetcher)
  const controller = new AbortController()
  expect(await TranscodingAPI.workerAssignments('node/一 & 二', controller.signal)).toEqual(rooms)
  const [path, options] = fetcher.mock.calls[0]!
  expect(path).toBe(`http://127.0.0.1:5784/api/transcoding/workers/${encodeURIComponent('node/一 & 二')}/assignments`)
  expect(options.signal).toBe(controller.signal)
  expect(options.body).toBeUndefined()
})
