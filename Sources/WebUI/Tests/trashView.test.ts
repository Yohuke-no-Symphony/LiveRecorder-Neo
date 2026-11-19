// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, type Pinia } from 'pinia'
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
  VTable: { template: '<table><slot /></table>' },
  VAlert: { template: '<div role="alert"><slot /></div>' },
  VBtn: { props: ['disabled', 'loading'], template: '<button :disabled="disabled || loading"><slot /></button>' },
  VDialog: { props: ['modelValue'], template: '<div v-if="modelValue" role="dialog"><slot /></div>' },
  VCheckboxBtn: {
    props: ['modelValue', 'disabled', 'indeterminate'],
    emits: ['update:modelValue'],
    template: '<input type="checkbox" :checked="modelValue" :disabled="disabled" :indeterminate="indeterminate" @change="$emit(\'update:modelValue\', $event.target.checked)" />',
  },
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

async function renderTrash(entries: TrashEntry[], role: UserProfile['role'] = 'Admin'): Promise<VueWrapper>
{
  serverEntries = entries
  useAuthStore(pinia).user = {
    id: 'owner', displayName: '测试用户', email: 'owner@example.test', role,
    status: 'Active', permissions: [PERMISSIONS.manageRecordings],
  }
  const wrapper = mount(TrashView, { global: { plugins: [pinia, i18n], stubs } })
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

    await button(wrapper, '选择').trigger('click')
    const selectAll = checkbox(wrapper, '全选可删除项目')
    const clipA = checkbox(wrapper, '选择 切片 a')
    const clipB = checkbox(wrapper, '选择 切片 b')
    const shared = wrapper.find<HTMLInputElement>('input[aria-label="选择 共享录像"]')
    expect(shared.exists() && !shared.element.disabled).toBe(false)
    expect(button(wrapper, '彻底删除所选').element.disabled).toBe(true)

    await clipA.setValue(true)
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

    await selectAll.setValue(false)
    expect(clipA.element.checked || clipB.element.checked).toBe(false)
    await button(wrapper, '取消选择').trigger('click')
    expect(wrapper.find('input[aria-label="全选可删除项目"]').exists()).toBe(false)
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
    expect(purgeTrashEntries).toHaveBeenCalledExactlyOnceWith(['a', 'b'])
    expect(trash).toHaveBeenCalledTimes(2)
    expect(storage).toHaveBeenCalledTimes(2)
    expect(storage).toHaveBeenLastCalledWith(true)
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    const rows = wrapper.get('tbody').text()
    expect(rows).toContain(recording.title)
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

    await button(wrapper, '选择').trigger('click')
    await checkbox(wrapper, '全选可删除项目').setValue(true)
    await button(wrapper, '彻底删除所选').trigger('click')
    await confirmPurge(wrapper)
    expect(purgeTrashEntries).toHaveBeenNthCalledWith(1, ['a', 'b'])
    expect(wrapper.get('tbody').text()).not.toContain('切片 a')
    expect(checkbox(wrapper, '选择 切片 b').element.checked).toBe(true)
    expect(dialog(wrapper).text()).toContain('文件暂时无法删除')
    expect(dialog(wrapper).text()).toContain('彻底删除这 1 项后无法恢复。')

    await confirmPurge(wrapper)
    expect(purgeTrashEntries).toHaveBeenNthCalledWith(2, ['b'])
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('回收站为空')
  })

  it('先分批删除分段再提交所属录像，中断后重试仍在回收站的未完成条目', async () =>
  {
    const recording = entry('session', { kind: 'Session', title: '回收录像' })
    const segments = Array.from({ length: 400 }, (_, index) => entry(`segment-${index}`, {
      kind: 'Segment', sessionId: recording.entityId, title: `分段 ${index}`,
    }))
    const wrapper = await renderTrash([recording, ...segments])
    purgeTrashEntries
      .mockImplementationOnce(async ids => completeBatch(ids))
      .mockImplementationOnce(async ids =>
      {
        removeEntries(ids.slice(0, 1))
        throw new Error('批次连接中断')
      })

    await button(wrapper, '清空').trigger('click')
    expect(dialog(wrapper).text()).toContain('彻底删除这 401 项后无法恢复。')
    await confirmPurge(wrapper)

    const attemptedBatches = purgeTrashEntries.mock.calls.map(([ids]) => ids)
    expect(attemptedBatches.map(ids => ids.length)).toEqual([200, 200])
    expect(new Set(attemptedBatches.flat())).toEqual(new Set(segments.map(item => item.id)))
    expect(dialog(wrapper).text()).toContain('批次连接中断')
    expect(dialog(wrapper).text()).toContain('彻底删除这 200 项后无法恢复。')
    const remaining = new Set(serverEntries.map(item => item.id))

    await confirmPurge(wrapper)
    expect(purgeTrashEntries).toHaveBeenCalledTimes(3)
    const [retriedIds] = purgeTrashEntries.mock.calls[2]!
    expect(new Set(retriedIds)).toEqual(remaining)
    expect(retriedIds.at(-1)).toBe(recording.id)
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('回收站为空')
  })
})
