// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import router from '../src/router'
import { useAuthStore } from '../src/stores/auth'

let pinia: Pinia

beforeEach(() =>
{
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() })
  vi.stubGlobal('scrollTo', vi.fn())
  pinia = createPinia()
  setActivePinia(pinia)
  const auth = useAuthStore()
  auth.user = { id: 'viewer', email: 'viewer@example.test', displayName: '观众', role: 'User', status: 'Active', permissions: [] }
  auth.installed = true
  auth.ready = true
  vi.spyOn(auth, 'bootstrap').mockResolvedValue()
  vi.spyOn(auth, 'refresh').mockResolvedValue()
})

afterEach(() =>
{
  disposePinia(pinia)
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('录像权限路由边界', () =>
{
  it('缺观看权限时媒体库和播放地址都进入账户页，登录页也不会循环重定向', async () =>
  {
    for (const path of ['/', '/sessions/private-recording', '/streamers/streamer', '/analytics', '/login'])
    {
      await router.push(path)
      expect(router.currentRoute.value.path).toBe('/account')
    }
  })

  it('独立授权恢复观看后可进入媒体库，个人任务取消与清理页无需观看权限', async () =>
  {
    for (const path of ['/jobs', '/clips', '/trash'])
    {
      await router.push(path)
      expect(router.currentRoute.value.path).toBe(path)
    }
    useAuthStore().user!.permissions = ['recordings.view']
    await router.push('/')
    expect(router.currentRoute.value.path).toBe('/')
  })
})
