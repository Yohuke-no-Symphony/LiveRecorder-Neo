// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { nextTick } from 'vue'
import TargetAddDialog from '../src/components/targets/TargetAddDialog.vue'
import { i18n } from '../src/plugins/i18n'
import type { BilibiliRoomSearchPage } from '../src/models/bilibili'
import type { Streamer } from '../src/models/media'

const { searchRooms, createStreamer } = vi.hoisted(() => ({ searchRooms: vi.fn(), createStreamer: vi.fn() }))
vi.mock('../src/apis/AuthAPI', () => ({ AuthAPI: { searchBilibiliRooms: searchRooms } }))
vi.mock('../src/apis/MediaAPI', () => ({ MediaAPI: { createStreamer } }))

const mounted: VueWrapper[] = []
const container = { template: '<div><slot /></div>' }
const target: Streamer = {
  id: 'target-1', createdAtUtc: '2026-11-13T00:00:00Z', uid: 42, roomId: 21573665,
  name: '主播', avatarUrl: '', enabled: true, isMonitored: true, isLive: true, state: 'Monitoring',
}
const result: BilibiliRoomSearchPage = {
  results: [{ roomId: '21573665', shortRoomId: '42', ownerUid: '3546873520458421', ownerName: '主播', title: '直播标题', isLive: true, sources: ['nickname'] }],
  issues: [], page: 1, hasMore: false,
}

function open(monitoredRoomIds: string[] = []): VueWrapper
{
  const wrapper = mount(TargetAddDialog, {
    props: {
      modelValue: true, monitoredRoomIds,
      accounts: [{ id: 'account-1', uid: 42, userName: '采集账号', avatarUrl: '', status: 'Valid' }],
    },
    global: {
      plugins: [i18n],
      stubs: {
        VDialog: container, VCard: container, VCardText: container, VCardActions: container,
        VTabs: { name: 'VTabs', props: ['modelValue'], template: '<nav><slot /></nav>' },
        VTab: { props: ['value', 'disabled'], template: '<button :disabled="disabled"><slot /></button>' },
        VWindow: container, VWindowItem: container, VChip: container, VSpacer: true, VProgressLinear: true,
        VBtn: { props: ['disabled', 'loading', 'type'], template: '<button :disabled="disabled || loading" :type="type || \'button\'"><slot /></button>' },
        VTextField: {
          props: ['modelValue', 'label', 'name', 'disabled'], emits: ['update:modelValue'],
          template: '<label>{{ label }}<input :name="name" :value="modelValue" :disabled="disabled" @input="$emit(\'update:modelValue\', $event.target.value)" /></label>',
        },
        VSelect: { name: 'VSelect', props: ['modelValue', 'label', 'items'], template: '<label>{{ label }}</label>' },
        VSwitch: { name: 'VSwitch', props: ['modelValue', 'label'], template: '<label>{{ label }}</label>' },
        VAlert: { template: '<div role="alert"><slot /></div>' },
      },
    },
  })
  mounted.push(wrapper)
  return wrapper
}

beforeEach(() =>
{
  i18n.global.locale.value = 'zh-CN'
  searchRooms.mockReset().mockResolvedValue(result)
  createStreamer.mockReset().mockResolvedValue(target)
})
afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  i18n.global.locale.value = 'zh-CN'
})

describe('直播间双 Tab 添加弹窗', () =>
{
  it('默认自动搜索，双 Tab 和共用设置支持三语且打开不请求平台', async () =>
  {
    const wrapper = open()
    expect(wrapper.findComponent({ name: 'VTabs' }).props('modelValue')).toBe('search')
    expect(searchRooms).not.toHaveBeenCalled()
    for (const [language, automatic, manual, account] of [
      ['zh-CN', '自动搜索', '手动添加', 'B站账号'],
      ['en-US', 'Search', 'Manual entry', 'Bilibili account'],
      ['ja-JP', '自動検索', '手動追加', 'Bilibili アカウント'],
    ] as const)
    {
      i18n.global.locale.value = language
      await nextTick()
      expect(wrapper.text()).toContain(automatic)
      expect(wrapper.text()).toContain(manual)
      expect(wrapper.text()).toContain(account)
    }
  })

  it('手动添加使用共享账号和录制开关，成功后提交结果并关闭', async () =>
  {
    const wrapper = open()
    wrapper.findComponent({ name: 'VTabs' }).vm.$emit('update:modelValue', 'manual')
    wrapper.findComponent({ name: 'VSelect' }).vm.$emit('update:modelValue', 'account-1')
    wrapper.findComponent({ name: 'VSwitch' }).vm.$emit('update:modelValue', false)
    await nextTick()
    await wrapper.get('input[name="roomId"]').setValue('21573665')
    await wrapper.get('form.manual-room-form').trigger('submit')
    await flushPromises()
    expect(createStreamer).toHaveBeenCalledWith({ roomId: 21573665, accountId: 'account-1', enabled: false })
    expect(wrapper.emitted('added')).toEqual([[target]])
    expect(wrapper.emitted('update:modelValue')).toEqual([[false]])
  })

  it('无效房号不会请求添加，切换 Tab 保留手动草稿', async () =>
  {
    const wrapper = open()
    await wrapper.get('input[name="roomId"]').setValue('9007199254740993')
    await wrapper.get('form.manual-room-form').trigger('submit')
    expect(createStreamer).not.toHaveBeenCalled()
    expect(wrapper.get('[role="alert"]').text()).toContain('请输入有效直播间号')
    await wrapper.get('input[name="roomId"]').setValue('21573665')
    wrapper.findComponent({ name: 'VTabs' }).vm.$emit('update:modelValue', 'search')
    await nextTick()
    wrapper.findComponent({ name: 'VTabs' }).vm.$emit('update:modelValue', 'manual')
    await nextTick()
    expect(wrapper.get<HTMLInputElement>('input[name="roomId"]').element.value).toBe('21573665')
  })

  it('搜索结果保留完整 UID，并可按规范房号添加', async () =>
  {
    const wrapper = open()
    await wrapper.get('input[name="roomSearch"]').setValue('主播')
    await wrapper.get('form.room-search-form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('3546873520458421')
    expect(wrapper.text()).toContain('直播标题')
    await wrapper.get('button[aria-label="添加 主播"]').trigger('click')
    await flushPromises()
    expect(createStreamer).toHaveBeenCalledWith({ roomId: 21573665, accountId: null, enabled: true })
    expect(wrapper.emitted('added')).toEqual([[target]])
  })

  it('已监控的搜索结果标记为已添加并禁止重复提交', async () =>
  {
    const wrapper = open(['21573665'])
    await wrapper.get('form.room-search-form').trigger('submit')
    await flushPromises()
    const button = wrapper.get<HTMLButtonElement>('button[aria-label="添加 主播"]')
    expect(button.element.disabled).toBe(true)
    expect(button.text()).toBe('已添加')
    await button.trigger('click')
    expect(createStreamer).not.toHaveBeenCalled()
  })

  it('保存失败保留弹窗和草稿，可再次提交', async () =>
  {
    createStreamer.mockRejectedValueOnce(new Error('上游暂不可用'))
    const wrapper = open()
    await wrapper.get('input[name="roomId"]').setValue('21573665')
    await wrapper.get('form.manual-room-form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('上游暂不可用')
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
    expect(wrapper.get<HTMLInputElement>('input[name="roomId"]').element.value).toBe('21573665')
    await wrapper.get('form.manual-room-form').trigger('submit')
    await flushPromises()
    expect(createStreamer).toHaveBeenCalledTimes(2)
    expect(wrapper.emitted('added')).toEqual([[target]])
  })

  it('关注登录提示随语言切换更新，不重新请求平台', async () =>
  {
    searchRooms.mockResolvedValueOnce({ results: [], issues: [{ sources: ['following'], code: 'followingLoginRequired', apiCode: -101, message: '' }], page: 1, hasMore: false })
    const wrapper = open()
    await wrapper.get('form.room-search-form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('请选择已登录的B站账号')
    i18n.global.locale.value = 'en-US'
    await nextTick()
    expect(wrapper.text()).toContain('Select a signed-in Bilibili account')
    expect(searchRooms).toHaveBeenCalledTimes(1)
  })
})
