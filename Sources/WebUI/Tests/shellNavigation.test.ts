// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import { nextTick } from 'vue'
import { createMemoryHistory, createRouter, type Router } from 'vue-router'
import { createVuetify } from 'vuetify'
import { VBreadcrumbs } from 'vuetify/components/VBreadcrumbs'
import AppShell from '../src/components/layout/AppShell.vue'
import applicationRouter from '../src/router'
import { useAuthStore } from '../src/stores/auth'
import { i18n } from '../src/plugins/i18n'

vi.mock('../src/composables/usePolling', () => ({ usePolling: vi.fn() }))

let pinia: Pinia
let router: Router
const mounted: VueWrapper[] = []
const container = { template: '<div><slot /></div>' }

beforeEach(() =>
{
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() })
  vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() })))
  pinia = createPinia()
  setActivePinia(pinia)
  router = createRouter({ history: createMemoryHistory(), routes: applicationRouter.options.routes })
  const auth = useAuthStore()
  auth.user = { id: 'viewer', email: 'viewer@example.test', displayName: '观众', role: 'User', status: 'Active', permissions: ['recordings.view'] }
  vi.spyOn(auth, 'refresh').mockResolvedValue()
  i18n.global.locale.value = 'zh-CN'
})

afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  disposePinia(pinia)
  i18n.global.locale.value = 'zh-CN'
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

async function openShell(path: string): Promise<VueWrapper>
{
  await router.push(path)
  const wrapper = mount(AppShell, {
    global: {
      plugins: [pinia, router, i18n, createVuetify({ components: { VBreadcrumbs } })],
      stubs: {
        VNavigationDrawer: container, VAppBar: container, VMain: container, VList: container,
        VListItem: { props: ['title', 'to'], template: '<span class="nav-item" :data-to="to">{{ title }}</span>' },
        VBtn: true, VMenu: true, VSnackbar: true, VSpacer: true, VIcon: true,
        VAppBarNavIcon: true, AppBrand: true, UserAvatar: true, StorageBar: true,
      },
    },
  })
  mounted.push(wrapper)
  return wrapper
}

function breadcrumbTitles(wrapper: VueWrapper): string[]
{
  return wrapper.findAll('.v-breadcrumbs-item').map(item => item.text())
}

describe('页面导航与面包屑', () =>
{
  it.each<[string, string[]]>([
    ['/', ['录播库']],
    ['/jobs', ['后台任务']],
    ['/clips', ['我的切片']],
    ['/analytics', ['直播分析']],
    ['/trash', ['回收站']],
    ['/account', ['账户设置']],
    ['/admin/targets', ['管理', '录制管理']],
    ['/admin/accounts', ['管理', 'B站账号']],
    ['/admin/users', ['管理', '用户与邀请']],
    ['/admin/storage', ['管理', '磁盘']],
    ['/admin/workers', ['管理', 'Worker']],
    ['/admin/settings', ['管理', '设置']],
  ])('%s 显示实际层级，当前页与管理分组没有链接', async (path, titles) =>
  {
    useAuthStore().user!.role = 'Admin'
    const wrapper = await openShell(path)
    expect(breadcrumbTitles(wrapper)).toEqual(titles)
    expect(wrapper.findAll('.v-breadcrumbs a')).toHaveLength(0)
    expect(wrapper.get('.v-breadcrumbs [aria-current="page"]').text()).toBe(titles.at(-1))
  })

  it.each([
    ['/streamers/streamer', '主播录播'],
    ['/sessions/recording', '播放录播'],
  ])('%s 的录播库父级可点击，当前页不可点击', async (path, title) =>
  {
    const wrapper = await openShell(path)
    expect(breadcrumbTitles(wrapper)).toEqual(['录播库', title])
    expect(wrapper.findAll('.v-breadcrumbs a')).toHaveLength(1)
    const parent = wrapper.get('.v-breadcrumbs a')
    expect(parent.text()).toBe('录播库')
    expect(parent.attributes('href')).toBe('/')
    expect(wrapper.get('.v-breadcrumbs [aria-current="page"]').text()).toBe(title)
    await parent.trigger('click')
    await vi.waitFor(() => expect(router.currentRoute.value.path).toBe('/'))
    expect(breadcrumbTitles(wrapper)).toEqual(['录播库'])
    expect(wrapper.findAll('.v-breadcrumbs a')).toHaveLength(0)
  })

  it('没有观看权限时不生成录播库父级链接或侧栏入口', async () =>
  {
    useAuthStore().user!.permissions = []
    const wrapper = await openShell('/sessions/recording')
    expect(breadcrumbTitles(wrapper)).toEqual(['录播库', '播放录播'])
    expect(wrapper.findAll('.v-breadcrumbs a')).toHaveLength(0)
    expect(wrapper.find('.nav-item[data-to="/"]').exists()).toBe(false)
    expect(wrapper.findAll('.nav-item[data-to^="/admin/"]')).toHaveLength(0)
  })

  it('Worker管理拥有独立的管理员路由和侧栏入口', async () =>
  {
    useAuthStore().user!.role = 'Admin'
    const wrapper = await openShell('/admin/workers')
    expect(router.resolve('/admin/workers').meta.admin).toBe(true)
    expect(wrapper.get('.nav-item[data-to="/admin/workers"]').text()).toBe('Worker')
    useAuthStore().user!.role = 'User'
    await nextTick()
    expect(wrapper.find('.nav-item[data-to="/admin/workers"]').exists()).toBe(false)
    await vi.waitFor(() => expect(router.currentRoute.value.path).toBe('/'))
  })

  it('同一外壳内切换页面会更新层级，未知路径按现有规则回到录播库', async () =>
  {
    useAuthStore().user!.role = 'Admin'
    const wrapper = await openShell('/admin/settings')
    await router.push('/jobs')
    expect(breadcrumbTitles(wrapper)).toEqual(['后台任务'])
    await router.push('/unknown-path')
    expect(router.currentRoute.value.path).toBe('/')
    expect(breadcrumbTitles(wrapper)).toEqual(['录播库'])
  })

  it('运行时切换三语，侧栏、面包屑、页面标题和返回按钮使用同一名称', async () =>
  {
    const wrapper = await openShell('/streamers/streamer')
    for (const [language, library, streamer] of [
      ['zh-CN', '录播库', '主播录播'],
      ['en-US', 'Recording library', 'Streamer recordings'],
      ['ja-JP', '録画ライブラリ', '配信者の録画'],
    ] as const)
    {
      i18n.global.locale.value = language
      await nextTick()
      expect(wrapper.get('.nav-item[data-to="/"]').text()).toBe(library)
      expect(breadcrumbTitles(wrapper)).toEqual([library, streamer])
      expect(i18n.global.t('views.library.title')).toBe(library)
      expect(i18n.global.t('views.common.library')).toBe(library)
    }
  })
})
