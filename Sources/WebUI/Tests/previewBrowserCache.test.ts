// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { i18n } from '../src/plugins/i18n'

function browserDatabase()
{
  const stores = new Map<string, Map<string, unknown>>()
  let failTransactions = false
  const database = {
    objectStoreNames: { contains: (name: string) => stores.has(name) },
    createObjectStore: (name: string) => stores.set(name, new Map()),
    close: vi.fn(),
    transaction: () =>
    {
      if (failTransactions) { throw new DOMException('Storage unavailable', 'QuotaExceededError') }
      let pending = 0
      const transaction: Record<string, unknown> = {
        objectStore: (name: string) =>
        {
          const values = stores.get(name)!
          function request(action: () => unknown)
          {
            pending++
            const result: Record<string, unknown> = {}
            queueMicrotask(() =>
            {
              result.result = action()
              ;(result.onsuccess as (() => void) | undefined)?.()
              pending--
              queueMicrotask(() => { if (!pending) { (transaction.oncomplete as (() => void) | undefined)?.() } })
            })
            return result
          }
          return {
            get: (key: string) => request(() => structuredClone(values.get(key))),
            getAll: () => request(() => structuredClone([...values.values()])),
            put: (value: { key: string }) => request(() => values.set(value.key, structuredClone(value))),
            delete: (key: string) => request(() => values.delete(key)),
          }
        },
      }
      return transaction
    },
  }
  return {
    stores, fail: () => { failTransactions = true },
    factory: { open: vi.fn(() =>
    {
      const request: Record<string, unknown> = { result: database }
      queueMicrotask(() =>
      {
        ;(request.onupgradeneeded as (() => void) | undefined)?.()
        ;(request.onsuccess as (() => void) | undefined)?.()
      })
      return request
    }) },
  }
}

beforeEach(() =>
{
  vi.resetModules()
  vi.useFakeTimers({ toFake: ['Date'] })
  vi.setSystemTime(new Date('2026-11-16T00:00:00Z'))
  i18n.global.locale.value = 'zh-CN'
})
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); vi.useRealTimers() })

describe('浏览器预览缓存', () =>
{
  it('缓存键隔离服务器、身份、场次、内容版本和界面语言', async () =>
  {
    const { previewCacheKey } = await import('../src/utilities/previewBrowserCache')
    const original = previewCacheKey('events', 'session-1', 'viewer-1', 'revision-1')
    expect(previewCacheKey('events', 'session-1', 'viewer-2', 'revision-1')).not.toBe(original)
    expect(previewCacheKey('events', 'session-1', 'viewer-1', 'revision-2')).not.toBe(original)
    expect(previewCacheKey('events', 'session-2', 'viewer-1', 'revision-1')).not.toBe(original)
    const { i18n: freshI18n } = await import('../src/plugins/i18n')
    freshI18n.global.locale.value = 'en-US'
    expect(previewCacheKey('events', 'session-1', 'viewer-1', 'revision-1')).not.toBe(original)
    freshI18n.global.locale.value = 'zh-CN'
    vi.stubEnv('DEV', true)
    vi.stubEnv('VITE_API_BASE_URL', 'https://other-server.example.test')
    expect(previewCacheKey('events', 'session-1', 'viewer-1', 'revision-1')).not.toBe(original)
  })

  it('完整写入的快照在页面模块重建后可复用，删除后不可读取', async () =>
  {
    const storage = browserDatabase()
    vi.stubGlobal('indexedDB', storage.factory)
    const cache = await import('../src/utilities/previewBrowserCache')
    const value = { version: 'v1', items: [{ sequence: 1, text: '原文' }] }
    await cache.writePreviewCache('key', value)
    vi.resetModules()
    const reopened = await import('../src/utilities/previewBrowserCache')
    expect(await reopened.readPreviewCache('key')).toEqual(value)
    await reopened.removePreviewCache('key')
    expect(await reopened.readPreviewCache('key')).toBeUndefined()
  })

  it('清理过期快照并限制缓存条目数，最近读取的快照受到保留', async () =>
  {
    vi.stubGlobal('indexedDB', browserDatabase().factory)
    const cache = await import('../src/utilities/previewBrowserCache')
    for (let index = 0; index < 32; index++)
    {
      vi.setSystemTime(new Date(Date.now() + 1000))
      await cache.writePreviewCache(String(index), { index })
    }
    vi.setSystemTime(new Date(Date.now() + 1000))
    expect(await cache.readPreviewCache('0')).toEqual({ index: 0 })
    await cache.writePreviewCache('latest', { index: 32 })
    expect(await cache.readPreviewCache('1')).toBeUndefined()
    expect(await cache.readPreviewCache('0')).toEqual({ index: 0 })
    vi.setSystemTime(new Date(Date.now() + 8 * 24 * 60 * 60 * 1000))
    expect(await cache.readPreviewCache('latest')).toBeUndefined()
  })

  it('没有IndexedDB、配额失败或数据库不可用时不影响播放数据加载', async () =>
  {
    vi.stubGlobal('indexedDB', undefined)
    const cache = await import('../src/utilities/previewBrowserCache')
    await expect(cache.writePreviewCache('key', { items: [] })).resolves.toBeUndefined()
    expect(await cache.readPreviewCache('key')).toBeUndefined()
    const storage = browserDatabase()
    vi.stubGlobal('indexedDB', storage.factory)
    await cache.writePreviewCache('key', { items: [] })
    storage.fail()
    await expect(cache.writePreviewCache('key', { items: ['new'] })).resolves.toBeUndefined()
    await expect(cache.removePreviewCache('key')).resolves.toBeUndefined()
    expect(await cache.readPreviewCache('key')).toBeUndefined()
  })

  it('IndexedDB不可用时直接降级，不为容量估算序列化完整快照', async () =>
  {
    vi.stubGlobal('indexedDB', undefined)
    const cache = await import('../src/utilities/previewBrowserCache')
    const toJSON = vi.fn(() => ({ items: [] }))
    await cache.writePreviewCache('key', { toJSON })
    expect(toJSON).not.toHaveBeenCalled()
  })

  it('临时打开失败后允许重试，不让失败的连接永久禁用浏览器缓存', async () =>
  {
    const storage = browserDatabase()
    storage.factory.open.mockImplementationOnce(() => { throw new DOMException('Temporarily unavailable', 'UnknownError') })
    vi.stubGlobal('indexedDB', storage.factory)
    const cache = await import('../src/utilities/previewBrowserCache')
    await cache.writePreviewCache('first', { items: [] })
    await cache.writePreviewCache('second', { items: [1] })
    expect(await cache.readPreviewCache('second')).toEqual({ items: [1] })
  })

  it('不完整或无法序列化的新数据不覆盖上次完整快照', async () =>
  {
    vi.stubGlobal('indexedDB', browserDatabase().factory)
    const cache = await import('../src/utilities/previewBrowserCache')
    await cache.writePreviewCache('key', { items: [1] })
    const invalid: Record<string, unknown> = {}
    invalid.self = invalid
    await cache.writePreviewCache('key', invalid)
    expect(await cache.readPreviewCache('key')).toEqual({ items: [1] })
  })
})
