// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { nextTick } from 'vue'
import { createPinia, disposePinia, type Pinia } from 'pinia'
import { createMemoryHistory, createRouter, type Router } from 'vue-router'
import { createVuetify } from 'vuetify'
import { VTextField } from 'vuetify/components/VTextField'
import { ApiError } from '../src/apis/APIBase'
import { MediaAPI } from '../src/apis/MediaAPI'
import ClipCard from '../src/components/clips/ClipCard.vue'
import ClipShareDialog from '../src/components/clips/ClipShareDialog.vue'
import ConfirmDialog from '../src/components/common/ConfirmDialog.vue'
import { PERMISSIONS } from '../src/models/auth'
import type { Clip } from '../src/models/media'
import { i18n } from '../src/plugins/i18n'
import { useAuthStore } from '../src/stores/auth'
import ClipShareView from '../src/views/ClipShareView.vue'

const clip: Clip = {
  id: 'clip-1', ownerId: 'owner', sessionId: 'session-1', jobId: 'clip-job', title: '测试切片',
  bytes: 2048, startMs: 10000, endMs: 16000, createdAtUtc: '2026-11-13T00:00:00Z',
  expiresAtUtc: '2026-11-20T00:00:00Z', isTrashed: false,
}
const token = 'test-link_token'
const cardPath = `/shares/clips/${clip.id}/${token}`
const apiOrigin = 'https://media.example.test'
const mounted: VueWrapper[] = []
const clipboardDescriptor = Object.getOwnPropertyDescriptor(navigator, 'clipboard')
const container = { template: '<div><slot /></div>' }
const stubs = {
  VCard: container, VCardText: container, VProgressLinear: true,
  VDialog: { props: ['modelValue'], template: '<section v-if="modelValue" role="dialog"><slot /></section>' },
  VSnackbar: { props: ['modelValue'], template: '<div v-if="modelValue" role="status"><slot /></div>' },
  VAlert: { template: '<div role="alert"><slot /><slot name="append" /></div>' },
  VBtn: {
    props: ['href', 'disabled', 'loading'],
    template: '<component :is="href ? \'a\' : \'button\'" :href="href" :disabled="disabled || loading"><slot /></component>',
  },
}
let pinia: Pinia
let router: Router

function globalOptions()
{
  return { plugins: [pinia, router, i18n, createVuetify({ components: { VTextField } })], stubs }
}

function openDialog(): VueWrapper
{
  useAuthStore(pinia).user!.id = clip.ownerId
  const wrapper = mount(ClipShareDialog, { props: { clip, modelValue: true }, global: globalOptions() })
  mounted.push(wrapper)
  return wrapper
}

async function openCard(): Promise<VueWrapper>
{
  await router.push(cardPath)
  const wrapper = mount(ClipShareView, { global: globalOptions() })
  mounted.push(wrapper)
  await flushPromises()
  return wrapper
}

function button(wrapper: Pick<VueWrapper, 'findAll'>, label: string)
{
  const match = wrapper.findAll('button').find(item => item.text() === label)
  if (!match) { throw new Error(`Missing button: ${label}`) }
  return match
}

function mountClipCard()
{
  const options = globalOptions()
  const activeClip = { ...clip, expiresAtUtc: new Date(Date.now() + 60000).toISOString() }
  const wrapper = mount(ClipCard, { props: { clip: activeClip }, global: {
    ...options,
    stubs: {
      ...options.stubs,
      RouterLink: { template: '<a><slot /></a>' },
      VImg: true,
      VIcon: true,
      VTooltip: { props: ['text', 'location'], template: '<slot name="activator" :props="{ \'data-tooltip\': text }" />' },
    },
  } })
  mounted.push(wrapper)
  return wrapper
}

beforeEach(async () =>
{
  vi.stubEnv('VITE_API_BASE_URL', apiOrigin)
  pinia = createPinia()
  useAuthStore(pinia).user = {
    id: 'viewer', email: 'viewer@example.test', displayName: '观众', role: 'User', status: 'Active',
    permissions: [PERMISSIONS.viewRecordings],
  }
  i18n.global.locale.value = 'zh-CN'
  const placeholder = { template: '<div />' }
  router = createRouter({ history: createMemoryHistory(), routes: [
    { path: '/', component: placeholder },
    { path: '/login', component: placeholder },
    { path: '/shares/clips/:id/:token', name: 'shared-clip', component: ClipShareView },
  ] })
  await router.push('/')
})

afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  disposePinia(pinia)
  if (clipboardDescriptor) { Object.defineProperty(navigator, 'clipboard', clipboardDescriptor) }
  else { Reflect.deleteProperty(navigator, 'clipboard') }
  vi.restoreAllMocks()
  vi.unstubAllEnvs()
})

describe('切片卡片操作', () =>
{
  it('同一个操作栏提供四个图标按钮的名称、提示和操作', async () =>
  {
    useAuthStore(pinia).user!.permissions.push(PERMISSIONS.downloadRecordings)
    const wrapper = mountClipCard()
    const actions = wrapper.get('.clip-actions').findAll('button, a')
    expect(actions.map(action => action.attributes('aria-label'))).toEqual(['播放', '下载', '切片分享', '移入回收站'])
    for (const action of actions)
    {
      expect(action.attributes('data-tooltip')).toBe(action.attributes('aria-label'))
      expect(action.text()).toBe('')
    }
    expect(wrapper.get('a[aria-label="下载"]').attributes('href')).toBe(`${apiOrigin}/api/clips/${clip.id}/download`)

    await wrapper.get('button[aria-label="播放"]').trigger('click')
    await wrapper.get('button[aria-label="切片分享"]').trigger('click')
    await wrapper.get('button[aria-label="移入回收站"]').trigger('click')
    expect(wrapper.emitted('preview')).toHaveLength(1)
    expect(wrapper.emitted('share')).toHaveLength(1)
    expect(wrapper.emitted('trash')).toHaveLength(1)
  })

  it('无下载权限或切片到期时无下载链接，到期仍可移入回收站', async () =>
  {
    const wrapper = mountClipCard()
    expect(wrapper.get('button[aria-label="下载"]').attributes('disabled')).toBeDefined()
    expect(wrapper.find('a[aria-label="下载"]').exists()).toBe(false)
    expect(wrapper.get('button[aria-label="播放"]').attributes('disabled')).toBeUndefined()

    useAuthStore(pinia).user!.permissions.push(PERMISSIONS.downloadRecordings)
    await nextTick()
    expect(wrapper.find('a[aria-label="下载"]').exists()).toBe(true)

    await wrapper.setProps({ clip: { ...clip, expiresAtUtc: '2000-01-01T00:00:00Z' } })
    expect(wrapper.find('a[aria-label="下载"]').exists()).toBe(false)
    for (const label of ['播放', '下载', '切片分享'])
    {
      expect(wrapper.get(`button[aria-label="${label}"]`).attributes('disabled')).toBeDefined()
    }
    await wrapper.get('button[aria-label="移入回收站"]').trigger('click')
    expect(wrapper.emitted('trash')).toHaveLength(1)
  })
})

describe('切片分享管理', () =>
{
  it('仅有查看权限也能创建链接，复制失败后可手动选中网站地址', async () =>
  {
    const status = vi.spyOn(MediaAPI, 'clipShare').mockResolvedValue({ token: null })
    const create = vi.spyOn(MediaAPI, 'createClipShare').mockResolvedValue({ token })
    const writeText = vi.fn().mockRejectedValue(new DOMException('Clipboard denied', 'NotAllowedError'))
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })
    const wrapper = openDialog()
    await flushPromises()
    expect(status).toHaveBeenCalledWith(clip.id)
    expect(wrapper.find('input').exists()).toBe(false)

    await button(wrapper, '创建分享链接').trigger('click')
    await flushPromises()
    expect(create).toHaveBeenCalledWith(clip.id)
    const link = new URL(cardPath, window.location.origin).href
    const input = wrapper.get<HTMLInputElement>('input')
    expect(input.element.readOnly).toBe(true)
    expect(input.element.value).toBe(link)

    await button(wrapper, '复制链接').trigger('click')
    await flushPromises()
    expect(writeText).toHaveBeenCalledWith(link)
    expect(wrapper.get('[role="dialog"] [role="status"]').text()).toBe('请选择链接并手动复制。')
    input.element.select()
    expect(input.element.selectionStart).toBe(0)
    expect(input.element.selectionEnd).toBe(link.length)
  })

  it('失去查看权限后仍能撤销已有链接，撤销后清除链接和复制状态', async () =>
  {
    useAuthStore(pinia).user!.permissions = []
    const status = vi.spyOn(MediaAPI, 'clipShare').mockResolvedValueOnce({ token }).mockResolvedValue({ token: null })
    const revoke = vi.spyOn(MediaAPI, 'revokeClipShare').mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: vi.fn().mockResolvedValue(undefined) } })
    const wrapper = openDialog()
    await flushPromises()
    expect(wrapper.get<HTMLInputElement>('input').element.value).toContain(cardPath)
    await button(wrapper, '复制链接').trigger('click')
    await flushPromises()
    const snackbar = wrapper.get('[role="status"]')
    expect(snackbar.text()).toBe('链接已复制')
    expect(snackbar.attributes('location')).toBe('bottom center')
    expect(wrapper.find('[role="dialog"] [role="status"]').exists()).toBe(false)

    await button(wrapper, '撤销分享').trigger('click')
    const confirm = wrapper.getComponent(ConfirmDialog)
    expect(confirm.text()).toContain('撤销后，已发出的这条链接将失效。')
    expect(revoke).not.toHaveBeenCalled()
    await button(confirm, '撤销分享').trigger('click')
    await flushPromises()

    expect(revoke).toHaveBeenCalledWith(clip.id, token)
    expect(status).toHaveBeenCalledTimes(2)
    expect(wrapper.find('input').exists()).toBe(false)
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
    expect(wrapper.get('[role="alert"]').text()).toBe('创建链接需要查看录播权限。')
    expect(wrapper.findAll('button').map(item => item.text())).toEqual(['关闭'])
  })
})

describe('共享切片接收页', () =>
{
  it('有查看权限可读卡片，有下载权限才显示文件下载入口', async () =>
  {
    const load = vi.spyOn(MediaAPI, 'sharedClip').mockResolvedValue({
      title: clip.title, durationMs: 6000, bytes: clip.bytes, expiresAtUtc: clip.expiresAtUtc,
    })
    const wrapper = await openCard()
    expect(load).toHaveBeenCalledWith(clip.id, token)
    expect(wrapper.get('h2').text()).toBe(clip.title)
    expect(wrapper.text()).toContain('00:00:06')
    expect(wrapper.text()).toContain('2 KB')
    expect(wrapper.get('time').attributes('datetime')).toBe(clip.expiresAtUtc)
    expect(wrapper.text()).toContain('下载此切片需要下载录播权限。')
    expect(wrapper.find('a[download]').exists()).toBe(false)

    useAuthStore(pinia).user!.permissions = [PERMISSIONS.viewRecordings, PERMISSIONS.downloadRecordings]
    await nextTick()
    expect(wrapper.get('a[download]').attributes('href')).toBe(`${apiOrigin}/api/clips/${clip.id}/share/${token}/download`)
    expect(wrapper.text()).not.toContain('下载此切片需要下载录播权限。')
  })

  it.each([404, 410])('链接失效时显示不可用提示且隐藏下载入口（%s）', async status =>
  {
    useAuthStore(pinia).user!.permissions.push(PERMISSIONS.downloadRecordings)
    vi.spyOn(MediaAPI, 'sharedClip').mockRejectedValue(new ApiError(status, 'Unavailable'))
    const wrapper = await openCard()
    expect(wrapper.get('[role="alert"]').text()).toBe('此分享链接已失效或切片已不可用。')
    expect(wrapper.find('h2').exists()).toBe(false)
    expect(wrapper.find('a[download]').exists()).toBe(false)
  })

  it('读取卡片时登录失效会带着原链接进入登录页', async () =>
  {
    vi.spyOn(MediaAPI, 'sharedClip').mockRejectedValue(new ApiError(401, 'Unauthorized'))
    await openCard()
    expect(router.currentRoute.value.path).toBe('/login')
    expect(router.currentRoute.value.query.returnTo).toBe(cardPath)
  })
})
