// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { nextTick } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'
import SessionLibrary from '../src/components/library/SessionLibrary.vue'
import SessionCard from '../src/components/library/SessionCard.vue'
import ConfirmDialog from '../src/components/common/ConfirmDialog.vue'
import type { BroadcastSession } from '../src/models/media'
import { i18n } from '../src/plugins/i18n'

const { authState, deleteSession, trashSessions, setSessionPermanent } = vi.hoisted(() => ({ authState: { isAdmin: true, manageRecordings: false, viewAnalytics: false }, deleteSession: vi.fn(), trashSessions: vi.fn(), setSessionPermanent: vi.fn() }))
vi.mock('../src/stores/auth', async () =>
{
  const { reactive } = await import('vue')
  const auth = reactive({
    get isAdmin() { return authState.isAdmin },
    get canManageRecordings() { return authState.isAdmin || authState.manageRecordings },
    get canViewAnalytics() { return authState.isAdmin || authState.viewAnalytics },
  })
  return { useAuthStore: () => auth }
})
vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: { cover: (id: string) => `/cover/${id}`, deleteSession, trashSessions, setSessionPermanent } }))

const mounted: VueWrapper[] = []
const container = { template: '<div><slot /></div>' }
const stubs = {
  VCard: container, VImg: container, VChip: container, VIcon: true, VSpacer: true, VDivider: true, VList: container,
  VBtn: { props: ['disabled', 'loading'], template: '<button :disabled="disabled || loading"><slot /></button>' },
  VListItem: { props: ['title', 'disabled'], template: '<button role="menuitem" :disabled="disabled">{{ title }}</button>' },
  VMenu: { props: ['modelValue', 'target'], template: '<div v-if="modelValue" role="menu"><slot /></div>' },
  VDialog: { props: ['modelValue'], template: '<div v-if="modelValue" role="dialog"><slot /></div>' },
  VSnackbar: { props: ['modelValue'], template: '<div v-if="modelValue" role="status"><slot /><slot name="actions" /></div>' },
  VAlert: { template: '<div role="alert"><slot /></div>' },
  VCheckboxBtn: {
    props: ['modelValue', 'disabled'], emits: ['update:modelValue'],
    template: '<input type="checkbox" :checked="modelValue" :disabled="disabled" @change="$emit(\'update:modelValue\', $event.target.checked)" />',
  },
}

function session(id: string, recording = false): BroadcastSession
{
  return {
    id, streamerId: 'streamer', roomId: 42, title: `录播 ${id}`, coverUrl: '', createdAtUtc: '2026-11-13T00:00:00Z',
    startedAtUtc: '2026-11-13T00:00:00Z', endedAtUtc: recording ? null : '2026-11-13T01:00:00Z',
    lastMediaAtUtc: '2026-11-13T01:00:00Z', state: recording ? 'Recording' : 'Completed', hasDanmu: true,
    totalBytes: 42, isTrashed: false, isPermanent: false, expiresAtUtc: recording ? null : '2026-11-20T01:00:00Z', mediaRevision: 1,
  }
}

async function start(sessions = [session('a'), session('b'), session('live', true)])
{
  const routeView = { template: '<div />' }
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: routeView }, { path: '/sessions/:id', component: routeView }, { path: '/analytics', component: routeView }, { path: '/trash', component: routeView }] })
  await router.push('/')
  let wrapper: VueWrapper<InstanceType<typeof SessionLibrary>>
  wrapper = mount(SessionLibrary, {
    props: { sessions,
      onRetentionChanged: (updated: BroadcastSession) => { void wrapper.setProps({ sessions: wrapper.props('sessions').map((item: BroadcastSession) => item.id === updated.id ? { ...item, isPermanent: updated.isPermanent, expiresAtUtc: updated.expiresAtUtc } : item) }) },
      onRemoved: (ids: string[]) => { void wrapper.setProps({ sessions: wrapper.props('sessions').filter((item: BroadcastSession) => !ids.includes(item.id)) }) } },
    global: { plugins: [router, i18n], stubs },
  })
  mounted.push(wrapper)
  return { wrapper, router }
}

function button(wrapper: VueWrapper, text: string)
{
  const found = wrapper.findAll('button').find(item => item.text() === text)
  if (!found) { throw new Error(`Missing button: ${text}`) }
  return found
}

async function selectAll(wrapper: VueWrapper): Promise<void>
{
  await button(wrapper, '多选').trigger('click')
  await button(wrapper, '全选已加载').trigger('click')
}

beforeEach(() =>
{
  authState.isAdmin = true
  authState.manageRecordings = false
  authState.viewAnalytics = false
  setSessionPermanent.mockReset().mockImplementation(async (id: string, isPermanent: boolean) => ({ ...session(id), isPermanent, expiresAtUtc: isPermanent ? null : '2026-11-20T01:00:00Z' }))
  i18n.global.locale.value = 'zh-CN'
  deleteSession.mockReset().mockResolvedValue(undefined)
  trashSessions.mockReset().mockImplementation(async (ids: string[]) => ids.map(sessionId => ({ sessionId, success: true })))
})
afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  i18n.global.locale.value = 'zh-CN'
})

describe('录播库操作', () =>
{
  it('右键打开菜单不进入播放，分析携带指定场次 ID', async () =>
  {
    const { wrapper, router } = await start()
    const event = new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 120, clientY: 240 })
    wrapper.get('a[href="/sessions/b"]').element.dispatchEvent(event)
    await nextTick()
    expect(event.defaultPrevented).toBe(true)
    expect(router.currentRoute.value.path).toBe('/')
    expect(wrapper.get('[role="menu"]').text()).toContain('直播分析')
    await button(wrapper, '直播分析').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.path).toBe('/analytics')
    expect(router.currentRoute.value.query).toEqual({ sessionId: 'b' })
  })

  it('触屏操作按钮和 Shift+F10 都可打开同一菜单', async () =>
  {
    const { wrapper } = await start()
    await wrapper.get('button[aria-label="录播 a 的操作"]').trigger('click')
    expect(wrapper.get('[role="menu"]').text()).toContain('选择 录播 a')
    await wrapper.get('a[href="/sessions/b"]').trigger('keydown', { key: 'F10', shiftKey: true })
    expect(wrapper.get('[role="menu"]').text()).toContain('选择 录播 b')
  })

  it('选择模式中的点击与空格只切换选择，取消后恢复播放导航', async () =>
  {
    const { wrapper, router } = await start()
    await button(wrapper, '多选').trigger('click')
    await wrapper.get('a[href="/sessions/a"]').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.path).toBe('/')
    expect(wrapper.get('input[aria-label="选择 录播 a"]').element).toHaveProperty('checked', true)
    await wrapper.get('a[href="/sessions/b"]').trigger('keydown', { key: ' ', code: 'Space' })
    expect(wrapper.get('input[aria-label="选择 录播 b"]').element).toHaveProperty('checked', true)
    await button(wrapper, '取消选择').trigger('click')
    await wrapper.get('a[href="/sessions/a"]').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.path).toBe('/sessions/a')
  })

  it('在录场次仍可分析，但禁止勾选和删除', async () =>
  {
    const { wrapper } = await start()
    await selectAll(wrapper)
    expect(wrapper.get('input[aria-label="选择 录播 live"]').element).toHaveProperty('disabled', true)
    expect(wrapper.text()).toContain('已选 2')
    await wrapper.get('a[href="/sessions/live"]').trigger('contextmenu')
    expect(button(wrapper, '移入回收站').element).toHaveProperty('disabled', true)
    expect(button(wrapper, '直播分析').element).toHaveProperty('disabled', false)
    await button(wrapper, '删除所选录播').trigger('click')
    expect(wrapper.get('[role="dialog"]').text()).toContain('将 2 场录播移入回收站')
    await button(wrapper, '移入回收站').trigger('click')
    await flushPromises()
    expect(trashSessions).toHaveBeenCalledWith(['a', 'b'])
  })

  it('单个删除先确认，取消不请求，再次确认后调用单场 API 并移出列表', async () =>
  {
    const { wrapper } = await start()
    await wrapper.get('a[href="/sessions/a"]').trigger('contextmenu')
    await button(wrapper, '移入回收站').trigger('click')
    expect(deleteSession).not.toHaveBeenCalled()
    await button(wrapper, '取消').trigger('click')
    expect(deleteSession).not.toHaveBeenCalled()
    await wrapper.get('a[href="/sessions/a"]').trigger('contextmenu')
    await button(wrapper, '移入回收站').trigger('click')
    await button(wrapper, '移入回收站').trigger('click')
    await flushPromises()
    expect(deleteSession).toHaveBeenCalledExactlyOnceWith('a')
    expect(trashSessions).not.toHaveBeenCalled()
    expect(wrapper.emitted('removed')).toEqual([[['a']]])
    expect(wrapper.find('a[href="/sessions/a"]').exists()).toBe(false)
  })

  it('批量部分失败只移出成功项，失败保留选中、原因和重试入口', async () =>
  {
    trashSessions.mockResolvedValueOnce([{ sessionId: 'a', success: true }, { sessionId: 'b', success: false, error: '正在处理切片' }])
    const { wrapper } = await start()
    await selectAll(wrapper)
    await wrapper.get('a[href="/sessions/a"]').trigger('contextmenu')
    await button(wrapper, '删除所选录播').trigger('click')
    await button(wrapper, '移入回收站').trigger('click')
    await flushPromises()
    expect(trashSessions).toHaveBeenCalledExactlyOnceWith(['a', 'b'])
    expect(wrapper.find('a[href="/sessions/a"]').exists()).toBe(false)
    expect(wrapper.get('input[aria-label="选择 录播 b"]').element).toHaveProperty('checked', true)
    expect(wrapper.get('[role="dialog"]').text()).toContain('正在处理切片')
    expect(wrapper.get('[role="dialog"]').text()).toContain('将 1 场录播移入回收站')
    await button(wrapper, '移入回收站').trigger('click')
    await flushPromises()
    expect(deleteSession).toHaveBeenCalledExactlyOnceWith('b')
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })

  it('网络失败不移出场次，保留选中并允许重试', async () =>
  {
    deleteSession.mockRejectedValueOnce(new Error('无法连接服务器'))
    const { wrapper } = await start([session('a')])
    await wrapper.get('button[aria-label="录播 a 的操作"]').trigger('click')
    await button(wrapper, '移入回收站').trigger('click')
    await button(wrapper, '移入回收站').trigger('click')
    await flushPromises()
    expect(wrapper.emitted('removed')).toBeUndefined()
    expect(wrapper.get('input[aria-label="选择 录播 a"]').element).toHaveProperty('checked', true)
    expect(wrapper.get('[role="dialog"]').text()).toContain('无法连接服务器')
    await button(wrapper, '移入回收站').trigger('click')
    await flushPromises()
    expect(deleteSession).toHaveBeenCalledTimes(2)
    expect(wrapper.emitted('removed')).toEqual([[['a']]])
  })

  it('确认期间录播恢复录制时阻止删除并提示重新选择', async () =>
  {
    const { wrapper } = await start([session('a')])
    await wrapper.get('a[href="/sessions/a"]').trigger('contextmenu')
    await button(wrapper, '移入回收站').trigger('click')
    await wrapper.setProps({ sessions: [session('a', true)] })
    await button(wrapper, '移入回收站').trigger('click')
    await flushPromises()
    expect(deleteSession).not.toHaveBeenCalled()
    expect(wrapper.get('[role="alert"]').text()).toContain('录播状态已更新')
  })

  it('请求期间忽略重复确认和关闭弹窗', async () =>
  {
    let finish: (() => void) | undefined
    deleteSession.mockReturnValueOnce(new Promise<void>(resolve => { finish = resolve }))
    const { wrapper } = await start([session('a')])
    await wrapper.get('a[href="/sessions/a"]').trigger('contextmenu')
    await button(wrapper, '移入回收站').trigger('click')
    const confirm = wrapper.findComponent(ConfirmDialog)
    confirm.vm.$emit('confirm')
    confirm.vm.$emit('confirm')
    confirm.vm.$emit('update:modelValue', false)
    await nextTick()
    expect(deleteSession).toHaveBeenCalledTimes(1)
    expect(confirm.props('modelValue')).toBe(true)
    finish?.()
    await flushPromises()
    expect(confirm.props('modelValue')).toBe(false)
  })

  it('全选最多200场，已选项可取消，新增选择不超限', async () =>
  {
    const { wrapper } = await start(Array.from({ length: 202 }, (_, index) => session(String(index))))
    await selectAll(wrapper)
    expect(wrapper.findAll('input:checked')).toHaveLength(200)
    expect(wrapper.get('input[aria-label="选择 录播 200"]').element).toHaveProperty('disabled', true)
    await wrapper.get('a[href="/sessions/201"]').trigger('click')
    expect(wrapper.findAll('input:checked')).toHaveLength(200)
    await wrapper.get('input[aria-label="选择 录播 0"]').setValue(false)
    await wrapper.get('input[aria-label="选择 录播 200"]').setValue(true)
    expect(wrapper.findAll('input:checked')).toHaveLength(200)
  })

  it('普通成员无分析和删除入口，保留原生右键和播放', async () =>
  {
    authState.isAdmin = false
    const { wrapper, router } = await start([session('a')])
    expect(wrapper.find('button').exists()).toBe(false)
    expect(wrapper.get('time').attributes('datetime')).toBe('2026-11-20T01:00:00Z')
    expect(wrapper.get('time').text()).toContain('到期')
    const event = new MouseEvent('contextmenu', { bubbles: true, cancelable: true })
    wrapper.get('a').element.dispatchEvent(event)
    await nextTick()
    expect(event.defaultPrevented).toBe(false)
    expect(wrapper.find('[role="menu"]').exists()).toBe(false)
    await wrapper.get('a').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.path).toBe('/sessions/a')
  })

  it('仅分析授权可以打开分析，无法选择、删除或永久保留', async () =>
  {
    authState.isAdmin = false
    authState.viewAnalytics = true
    const { wrapper } = await start([session('a')])
    await wrapper.get('a').trigger('contextmenu')
    expect(button(wrapper, '直播分析').exists()).toBe(true)
    expect(wrapper.text()).not.toContain('移入回收站')
    expect(wrapper.text()).not.toContain('设为永久保留')
    expect(wrapper.findAll('button').some(item => item.text() === '多选')).toBe(false)
  })

  it('仅管理授权可以设置和取消永久保留，保存失败时保留原状态', async () =>
  {
    authState.isAdmin = false
    authState.manageRecordings = true
    const { wrapper } = await start([session('a')])
    expect(wrapper.get('time').attributes('datetime')).toBe('2026-11-20T01:00:00Z')
    await wrapper.get('a').trigger('contextmenu')
    expect(wrapper.text()).not.toContain('直播分析')
    await button(wrapper, '设为永久保留').trigger('click')
    await flushPromises()
    expect(setSessionPermanent).toHaveBeenCalledExactlyOnceWith('a', true)
    expect(wrapper.props('sessions')[0]?.isPermanent).toBe(true)
    expect(wrapper.props('sessions')[0]?.expiresAtUtc).toBeNull()
    expect(wrapper.find('time').exists()).toBe(false)
    await wrapper.get('a').trigger('contextmenu')
    setSessionPermanent.mockRejectedValueOnce(new Error('无法保存'))
    await button(wrapper, '取消永久保留').trigger('click')
    await flushPromises()
    expect(wrapper.props('sessions')[0]?.isPermanent).toBe(true)
    expect(wrapper.get('[role="alert"]').text()).toContain('无法保存')
    expect(wrapper.find('time').exists()).toBe(false)
    const updated = { ...session('a'), expiresAtUtc: '2026-12-01T01:00:00Z' }
    setSessionPermanent.mockResolvedValueOnce(updated)
    await wrapper.get('a').trigger('contextmenu')
    await button(wrapper, '取消永久保留').trigger('click')
    await flushPromises()
    expect(wrapper.props('sessions')[0]?.isPermanent).toBe(false)
    expect(wrapper.get('time').attributes('datetime')).toBe(updated.expiresAtUtc)
  })

  it('相同卡片组件在三语下提供明确的操作名称', async () =>
  {
    const { wrapper } = await start([session('a')])
    expect(wrapper.findComponent(SessionCard).exists()).toBe(true)
    for (const [locale, name] of [['zh-CN', '录播 a 的操作'], ['en-US', 'Actions for 录播 a'], ['ja-JP', '录播 a の操作']] as const)
    {
      i18n.global.locale.value = locale
      await nextTick()
      expect(wrapper.find(`button[aria-label="${name}"]`).exists()).toBe(true)
    }
  })
})
