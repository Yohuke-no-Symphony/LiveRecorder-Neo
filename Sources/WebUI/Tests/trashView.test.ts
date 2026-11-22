// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, type Pinia } from 'pinia'
import { createVuetify } from 'vuetify'
import { VDataTable } from 'vuetify/components/VDataTable'
import { MediaAPI } from '../src/apis/MediaAPI'
import { PERMISSIONS, type UserProfile } from '../src/models/auth'
import type { TrashEntry } from '../src/models/media'
import { i18n } from '../src/plugins/i18n'
import { useAuthStore } from '../src/stores/auth'
import TrashView from '../src/views/TrashView.vue'

vi.mock('../src/apis/MediaAPI', () => ({
  MediaAPI: { trash: vi.fn(), storage: vi.fn(), restoreTrash: vi.fn(), purgeTrash: vi.fn(), purgeTrashEntries: vi.fn() },
}))

const trash = vi.mocked(MediaAPI.trash)
const storage = vi.mocked(MediaAPI.storage)
const purgeTrashEntries = vi.mocked(MediaAPI.purgeTrashEntries)
const container = { template: '<div><slot /></div>' }
const stubs = {
  VCard: container,
  VCardText: container,
  VTabs: { name: 'VTabs', props: ['modelValue'], emits: ['update:modelValue'], template: '<div role="tablist"><slot /></div>' },
  VTab: { props: ['value', 'disabled'], template: '<button role="tab" :disabled="disabled"><slot /></button>' },
  VAlert: { template: '<div role="alert"><slot /></div>' },
  VBtn: { props: ['disabled', 'loading'], template: '<button :disabled="disabled || loading"><slot /></button>' },
  VDialog: { props: ['modelValue'], template: '<div v-if="modelValue" role="dialog"><slot /></div>' },
  VIcon: true,
  VSnackbar: { props: ['modelValue'], template: '<div v-if="modelValue" role="status"><slot /><slot name="actions" /></div>' },
  RouterLink: true,
}
const mounted: VueWrapper[] = []
let pinia: Pinia
let serverEntries: TrashEntry[]

function entry(id: string, overrides: Partial<TrashEntry> = {}): TrashEntry
{
  return {
    id, createdAtUtc: '2026-11-13T00:00:00Z', ownerId: 'owner', kind: 'Clip', entityId: `${id}-media`,
    title: `切片 ${id}`, bytes: 42, purgeAtUtc: null, state: 'Active', ...overrides,
  }
}

function removeEntries(ids: readonly string[]): void
{
  const removed = new Set(ids)
  serverEntries = serverEntries.filter(item => !removed.has(item.id))
}

function completeBatch(ids: string[])
{
  removeEntries(ids)
  return ids.map(entryId => ({ entryId, success: true }))
}

async function renderTrash(entries: TrashEntry[], role: UserProfile['role'] = 'Admin', management = false,
  permissions: string[] = [PERMISSIONS.manageRecordings]): Promise<VueWrapper>
{
  serverEntries = entries
  useAuthStore(pinia).user = {
    id: 'owner', displayName: '测试用户', email: 'owner@example.test', role,
    status: 'Active', permissions,
  }
  const wrapper = mount(TrashView, {
    props: { management },
    global: { plugins: [pinia, i18n, createVuetify({ components: { VDataTable } })], stubs },
  })
  mounted.push(wrapper)
  await flushPromises()
  return wrapper
}

function button(scope: Pick<VueWrapper, 'findAll'>, label: string)
{
  const match = scope.findAll('button').find(item => item.text() === label)
  if (!match) { throw new Error(`找不到按钮「${label}」`) }
  return match
}

function checkbox(wrapper: VueWrapper, label: string)
{
  return wrapper.get<HTMLInputElement>(`input[aria-label="${label}"]`)
}

function rowCheckbox(wrapper: VueWrapper, title: string)
{
  const row = wrapper.findAll('tbody tr').find(item => item.text().includes(title))
  return row!.get<HTMLInputElement>('input[type="checkbox"]')
}

function dialog(wrapper: VueWrapper)
{
  return wrapper.get('[role="dialog"]')
}

async function confirmPurge(wrapper: VueWrapper): Promise<void>
{
  await button(dialog(wrapper), '彻底删除').trigger('click')
  await flushPromises()
}

beforeEach(() =>
{
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() })
  vi.stubGlobal('matchMedia', () => ({ matches: false, addEventListener() {}, removeEventListener() {} }))
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  pinia = createPinia()
  i18n.global.locale.value = 'zh-CN'
  trash.mockReset().mockImplementation(async () => [...serverEntries])
  storage.mockReset().mockResolvedValue([])
  purgeTrashEntries.mockReset().mockImplementation(async ids => completeBatch(ids))
})

afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  disposePinia(pinia)
  vi.unstubAllGlobals()
})

describe('回收站批量彻底删除', () =>
{
  it('录像管理者只可选择自己的切片，可全选、半选和取消', async () =>
  {
    const recording = entry('session', { kind: 'Session', title: '共享录像', ownerId: 'recorder' })
    const wrapper = await renderTrash([entry('a'), recording, entry('b')], 'User')

    const selectAll = checkbox(wrapper, '全选可删除项目')
    const clipA = rowCheckbox(wrapper, '切片 a')
    const clipB = rowCheckbox(wrapper, '切片 b')
    expect(wrapper.get('tbody').text()).not.toContain('共享录像')

    await clipA.trigger('click')
    expect(selectAll.element.indeterminate).toBe(true)
    expect(wrapper.text()).toContain('已选 1 项')
    await selectAll.setValue(true)
    expect(clipA.element.checked && clipB.element.checked).toBe(true)
    expect(selectAll.element.indeterminate).toBe(false)
    expect(wrapper.text()).toContain('已选 2 项')

    await button(wrapper, '彻底删除所选').trigger('click')
    expect(dialog(wrapper).text()).toContain('彻底删除这 2 项后无法恢复。')
    expect(purgeTrashEntries).not.toHaveBeenCalled()
    await button(dialog(wrapper), '取消').trigger('click')
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    expect(clipA.element.checked && clipB.element.checked).toBe(true)

    await button(wrapper, '取消选择').trigger('click')
    expect(clipA.element.checked || clipB.element.checked).toBe(false)
    expect(selectAll.element.checked).toBe(false)
    expect(purgeTrashEntries).not.toHaveBeenCalled()
  })

  it('清空按有权删除的确认快照提交，保留新增项并提示空间刷新失败', async () =>
  {
    const recording = entry('session', { kind: 'Session', title: '共享录像', ownerId: 'recorder' })
    const wrapper = await renderTrash([entry('a'), recording, entry('b')], 'User')

    await button(wrapper, '清空').trigger('click')
    expect(dialog(wrapper).text()).toContain('彻底删除这 2 项后无法恢复。')
    const newClip = entry('new', { title: '确认后进入回收站的切片' })
    serverEntries = [...serverEntries, newClip]
    storage.mockRejectedValueOnce(new Error('空间统计刷新失败'))

    await confirmPurge(wrapper)
    expect(purgeTrashEntries).toHaveBeenCalledExactlyOnceWith(['a', 'b'], false)
    expect(trash).toHaveBeenCalledTimes(2)
    expect(storage).toHaveBeenCalledTimes(2)
    expect(storage).toHaveBeenLastCalledWith(true)
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    const rows = wrapper.get('tbody').text()
    expect(rows).not.toContain(recording.title)
    expect(rows).toContain(newClip.title)
    expect(rows).not.toContain('切片 a')
    expect(rows).not.toContain('切片 b')
    expect(wrapper.get('[role="alert"]').text()).toContain('空间统计刷新失败')

    await button(wrapper, '刷新').trigger('click')
    await flushPromises()
    expect(purgeTrashEntries).toHaveBeenCalledTimes(1)
    expect(wrapper.get('tbody').text()).toContain(newClip.title)
  })

  it('同批部分失败时保留原因和失败项，重试只提交失败项', async () =>
  {
    const wrapper = await renderTrash([entry('a'), entry('b')])
    purgeTrashEntries.mockImplementationOnce(async () => [
      ...completeBatch(['a']),
      { entryId: 'b', success: false, error: '文件暂时无法删除' },
    ])

    await checkbox(wrapper, '全选可删除项目').setValue(true)
    await button(wrapper, '彻底删除所选').trigger('click')
    await confirmPurge(wrapper)
    expect(purgeTrashEntries).toHaveBeenNthCalledWith(1, ['a', 'b'], false)
    expect(wrapper.get('tbody').text()).not.toContain('切片 a')
    expect(rowCheckbox(wrapper, '切片 b').element.checked).toBe(true)
    expect(dialog(wrapper).text()).toContain('文件暂时无法删除')
    expect(dialog(wrapper).text()).toContain('彻底删除这 1 项后无法恢复。')

    await confirmPurge(wrapper)
    expect(purgeTrashEntries).toHaveBeenNthCalledWith(2, ['b'], false)
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('回收站为空')
  })

  it('先分批删除分段再提交所属录像，中断后重试仍在回收站的未完成条目', async () =>
  {
    const recording = entry('session', { kind: 'Session', title: '回收录像' })
    const segments = Array.from({ length: 400 }, (_, index) => entry(`segment-${index}`, {
      kind: 'Segment', sessionId: recording.entityId, title: `分段 ${index}`,
    }))
    const wrapper = await renderTrash([recording, ...segments], 'Admin', true)
    purgeTrashEntries
      .mockImplementationOnce(async ids => completeBatch(ids))
      .mockImplementationOnce(async ids =>
      {
        removeEntries(ids.slice(0, 1))
        throw new Error('批次连接中断')
      })

    await button(wrapper, '清空').trigger('click')
    expect(dialog(wrapper).text()).toContain('彻底删除 401 项（录播 / 录像分段）后无法恢复。')
    await confirmPurge(wrapper)

    const attemptedBatches = purgeTrashEntries.mock.calls.map(([ids]) => ids)
    expect(attemptedBatches.map(ids => ids.length)).toEqual([200, 200])
    expect(new Set(attemptedBatches.flat())).toEqual(new Set(segments.map(item => item.id)))
    expect(dialog(wrapper).text()).toContain('批次连接中断')
    expect(dialog(wrapper).text()).toContain('彻底删除 200 项（录播 / 录像分段）后无法恢复。')
    const remaining = new Set(serverEntries.map(item => item.id))

    await confirmPurge(wrapper)
    expect(purgeTrashEntries).toHaveBeenCalledTimes(3)
    const [retriedIds] = purgeTrashEntries.mock.calls[2]!
    expect(new Set(retriedIds)).toEqual(remaining)
    expect(retriedIds.at(-1)).toBe(recording.id)
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('回收站为空')
  })

  it('管理员的个人回收站也只显示自己的切片', async () =>
  {
    const wrapper = await renderTrash([entry('mine'), entry('other', { ownerId: 'other' }),
      entry('session', { kind: 'Session', title: '共享录像' })])
    expect(wrapper.get('h1').text()).toBe('个人回收站')
    expect(wrapper.find('[role="tablist"]').exists()).toBe(false)
    expect(wrapper.get('tbody').text()).toContain('切片 mine')
    expect(wrapper.get('tbody').text()).not.toContain('切片 other')
    expect(wrapper.get('tbody').text()).not.toContain('共享录像')
  })

  it('管理回收站切换类型时清除选中项，清空只提交当前类型', async () =>
  {
    const recording = entry('session', { kind: 'Session', title: '共享录像' })
    const segment = entry('segment', { kind: 'Segment', title: '片段甲' })
    const wrapper = await renderTrash([entry('other', { ownerId: 'other' }), recording, segment], 'Admin', true)
    expect(wrapper.get('h1').text()).toBe('回收站管理')
    expect(wrapper.findAll('[role="tab"]').map(tab => tab.text())).toEqual(['全部', '切片', '录播', '录像分段'])
    await checkbox(wrapper, '全选可删除项目').setValue(true)
    expect(wrapper.text()).toContain('已选 3 项')
    wrapper.getComponent({ name: 'VTabs' }).vm.$emit('update:modelValue', 'Clip')
    await flushPromises()
    expect(checkbox(wrapper, '全选可删除项目').element.checked).toBe(false)
    expect(wrapper.text()).not.toContain('已选')
    expect(wrapper.get('tbody').text()).not.toContain(recording.title)

    await button(wrapper, '清空').trigger('click')
    expect(dialog(wrapper).text()).toContain('清空「切片」？')
    expect(dialog(wrapper).text()).toContain('彻底删除 1 项（切片）后无法恢复。')
    await confirmPurge(wrapper)
    expect(purgeTrashEntries).toHaveBeenCalledExactlyOnceWith(['other'], true)
    expect(serverEntries.map(item => item.id)).toEqual([recording.id, segment.id])
  })

  it('切片与录播清理权限各自决定可见的类型和操作', async () =>
  {
    const wrapper = await renderTrash(
      [entry('other', { ownerId: 'other' }), entry('session', { kind: 'Session', title: '共享录像' })],
      'User', true, [PERMISSIONS.manageClips],
    )
    expect(wrapper.findAll('[role="tab"]').map(tab => tab.text())).toEqual(['全部', '切片'])
    expect(wrapper.get('tbody').text()).toContain('切片 other')
    expect(wrapper.get('tbody').text()).not.toContain('共享录像')
    expect(wrapper.get('tbody').text()).toContain('恢复')

    useAuthStore(pinia).user!.permissions = [PERMISSIONS.purgeRecordings]
    await flushPromises()
    expect(wrapper.findAll('[role="tab"]').map(tab => tab.text())).toEqual(['全部', '录播', '录像分段'])
    expect(wrapper.get('tbody').text()).toContain('共享录像')
    expect(wrapper.get('tbody').text()).not.toContain('切片 other')
    expect(wrapper.get('tbody').text()).not.toContain('恢复')
    expect(wrapper.find('tbody button[aria-label="彻底删除"]').exists()).toBe(true)
  })

  it('大小按字节排序，表头全选跨页且跳过只有恢复权限的录播', async () =>
  {
    const recording = entry('session', { kind: 'Session', title: '共享录像', bytes: 1 })
    const clips = Array.from({ length: 26 }, (_, index) => entry(String(index + 1), {
      title: `片段 ${index + 1}`, bytes: (26 - index) * 1024,
    }))
    const wrapper = await renderTrash([recording, ...clips], 'User', true,
      [PERMISSIONS.manageClips, PERMISSIONS.manageRecordings])
    const sizeHeader = wrapper.findAll('th').find(header => header.text() === '大小')!
    await sizeHeader.trigger('click')
    expect(wrapper.findAll('tbody tr').slice(0, 2).map(row => row.text())).toEqual([
      expect.stringContaining('共享录像'), expect.stringContaining('片段 26'),
    ])
    expect(rowCheckbox(wrapper, '共享录像').element.disabled).toBe(true)
    expect(wrapper.findAll('tbody tr')).toHaveLength(25)
    await checkbox(wrapper, '全选可删除项目').setValue(true)
    expect(wrapper.text()).toContain('已选 26 项')
    await button(wrapper, '彻底删除所选').trigger('click')
    await confirmPurge(wrapper)
    expect(purgeTrashEntries).toHaveBeenCalledExactlyOnceWith(clips.map(clip => clip.id), true)
    expect(wrapper.get('tbody').text()).toContain('共享录像')
  })
})
