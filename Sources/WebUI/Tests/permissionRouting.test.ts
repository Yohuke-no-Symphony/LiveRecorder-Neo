// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import { PERMISSIONS } from '../src/models/auth'
import { i18n } from '../src/plugins/i18n'
import router from '../src/router'
import { useAuthStore } from '../src/stores/auth'
import LoginView from '../src/views/LoginView.vue'

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

describe('分享链接路由边界', () =>
{
  const links = ['/shares/clips/clip-1/test-link_token', '/sessions/recording-1?at=123456']

  it.each(links)('未登录访问 %s 后，登录回到含原参数的链接', async link =>
  {
    await router.replace('/account')
    const auth = useAuthStore()
    const viewer = { ...auth.user!, permissions: [PERMISSIONS.viewRecordings] }
    auth.user = null
    await router.push(link)
    expect(router.currentRoute.value.path).toBe('/login')
    expect(router.currentRoute.value.query.returnTo).toBe(link)

    vi.spyOn(auth, 'login').mockImplementation(async () => { auth.user = viewer })
    const wrapper = mount(LoginView, {
      global: {
        plugins: [pinia, router, i18n],
        stubs: { AppBrand: true, VCard: { template: '<div><slot /></div>' }, VTextField: true, VBtn: true, VIcon: true },
      },
    })
    try
    {
      await wrapper.get('form').trigger('submit')
      await vi.waitFor(() => expect(router.currentRoute.value.fullPath).toBe(link))
    }
    finally
    {
      wrapper.unmount()
    }
  })

  it.each(links)('访问 %s 需要查看权限，下载权限独立', async link =>
  {
    await router.replace('/account')
    const auth = useAuthStore()
    auth.user!.permissions = [PERMISSIONS.downloadRecordings]
    await router.push(link)
    expect(router.currentRoute.value.path).toBe('/account')

    auth.user!.permissions = [PERMISSIONS.viewRecordings]
    await router.push(link)
    expect(router.currentRoute.value.fullPath).toBe(link)
  })
})
