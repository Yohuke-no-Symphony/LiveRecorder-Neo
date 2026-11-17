// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import { defineComponent, h, nextTick } from 'vue'
import { AuthAPI } from '../src/apis/AuthAPI'
import { MediaAPI } from '../src/apis/MediaAPI'
import { i18n } from '../src/plugins/i18n'
import type { ServerSettings } from '../src/models/settings'
import SettingsView from '../src/views/SettingsView.vue'

const tabs = defineComponent({ props: ['modelValue'], emits: ['update:modelValue'], setup: (_, { slots }) => () => h('div', slots.default?.()) })
const field = defineComponent({
  props: ['modelValue', 'label', 'hint', 'disabled'], emits: ['update:modelValue'],
  setup: (props, { emit }) => () => h('label', [props.label, h('input', {
    value: props.modelValue, 'aria-label': props.label, disabled: props.disabled,
    onInput: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).value),
  }), props.hint]),
})
const container = { template: '<div><slot /></div>' }
const stubs = {
  VTabs: tabs, VTab: container, VTextField: field, VSwitch: true, VSelect: true, VProgressLinear: true, VIcon: true,
  VBtn: { props: ['disabled', 'loading'], template: '<button :disabled="disabled || loading"><slot /></button>' },
  VAlert: { template: '<div role="alert"><slot /></div>' }, VSnackbar: { props: ['modelValue'], template: '<div v-if="modelValue" role="status"><slot /></div>' },
  ContentCard: container, DataDirectorySettings: true, LanguageSelect: true,
}
const mounted: VueWrapper[] = []
let pinia: Pinia
let current: ServerSettings

async function open(): Promise<VueWrapper>
{
  const wrapper = mount(SettingsView, { global: { plugins: [pinia, i18n], stubs } })
  mounted.push(wrapper)
  await flushPromises()
  wrapper.findComponent(tabs).vm.$emit('update:modelValue', 'media')
  await nextTick()
  return wrapper
}

async function save(wrapper: VueWrapper): Promise<void>
{
  await wrapper.get('header button').trigger('click')
  await flushPromises()
}

beforeEach(() =>
{
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() })
  pinia = createPinia(); setActivePinia(pinia)
  i18n.global.locale.value = 'zh-CN'
  current = {
    id: 'server', defaultLanguage: 'zh-CN', translationDanmuPattern: '.*?【.*?】', timeZone: 'Asia/Shanghai', quality: 10000,
    recordDanmu: true, segmentBytes: 1024 ** 3, recordingRetentionDays: 0, clipRetentionDays: 7, trashRetentionDays: 7, taskLogRetentionDays: 7,
    cacheLimitBytes: 1024 ** 3, cacheRetentionMinutes: 30, minimumFreeBytes: 1024 ** 3,
    mediaConcurrency: 2, playbackConcurrency: 2, monitorIntervalSeconds: 15, sessionGraceMinutes: 5, streamDisconnectTimeoutMinutes: 5,
    defaultUserPermissions: [], registrationEnabled: true, registrationApprovalRequired: true, registrationInvitationRequired: true,
    registrationEmailVerificationRequired: true, ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe', oauthProviders: [],
    smtp: { host: '', port: 587, useSsl: true, checkCertificateRevocation: true, userName: '', password: '', fromAddress: '', fromName: '' },
  }
  vi.spyOn(MediaAPI, 'settings').mockImplementation(async () => structuredClone(current))
  vi.spyOn(MediaAPI, 'updateSettings').mockImplementation(async value =>
  {
    current = JSON.parse(JSON.stringify(value)) as ServerSettings
    return structuredClone(current)
  })
  vi.spyOn(MediaAPI, 'settingsPaths').mockResolvedValue({ dataDirectory: '/srv/Data', recordsDirectory: '/srv/records', cacheDirectory: '/srv/Data/cache', configFile: '/srv/settings.json', configuredDataDirectory: 'Data', configuredRecordsDirectory: 'records', restartRequired: false })
  vi.spyOn(AuthAPI, 'providers').mockResolvedValue([])
})
afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  disposePinia(pinia); vi.restoreAllMocks(); vi.unstubAllGlobals()
  i18n.global.locale.value = 'zh-CN'
})

describe('任务日志保留设置', () =>
{
  it('旧响应缺字段时显示默认7天，编辑前不保存并明确现有日志与0的含义', async () =>
  {
    Reflect.deleteProperty(current, 'taskLogRetentionDays')
    const wrapper = await open()
    expect(wrapper.get<HTMLInputElement>('input[aria-label="任务日志保留（天）"]').element.value).toBe('7')
    expect(wrapper.text()).toContain('0 表示长期保留')
    expect(wrapper.text()).toContain('尚存日志')
    expect(MediaAPI.updateSettings).not.toHaveBeenCalled()
  })

  it.each([0, 1, 30, 3650])('显式保存%s天并在重开后保留', async days =>
  {
    const wrapper = await open()
    await wrapper.get('input[aria-label="任务日志保留（天）"]').setValue(String(days))
    expect(MediaAPI.updateSettings).not.toHaveBeenCalled()
    await save(wrapper)
    expect(MediaAPI.updateSettings).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ taskLogRetentionDays: days }))
    const reopened = await open()
    expect(reopened.get<HTMLInputElement>('input[aria-label="任务日志保留（天）"]').element.value).toBe(String(days))
  })

  it.each(['-1', '1.5', '3651', ''])('拒绝无效值“%s”且保留草稿', async value =>
  {
    const wrapper = await open()
    await wrapper.get('input[aria-label="任务日志保留（天）"]').setValue(value)
    await save(wrapper)
    expect(MediaAPI.updateSettings).not.toHaveBeenCalled()
    expect(wrapper.get('[role="alert"]').text()).toContain('0 至 3650')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="任务日志保留（天）"]').element.value).toBe(value)
  })

  it('保存失败保留编辑值，三语切换更新标签与说明', async () =>
  {
    vi.mocked(MediaAPI.updateSettings).mockRejectedValueOnce(new Error('设置保存失败'))
    const wrapper = await open()
    await wrapper.get('input[aria-label="任务日志保留（天）"]').setValue('21')
    await save(wrapper)
    expect(wrapper.get('[role="alert"]').text()).toBe('设置保存失败')
    for (const [locale, label] of [['en-US', 'Task log retention (days)'], ['ja-JP', 'タスクログの保存期間（日）'], ['zh-CN', '任务日志保留（天）']] as const)
    {
      i18n.global.locale.value = locale; await nextTick()
      expect(wrapper.get<HTMLInputElement>(`input[aria-label="${label}"]`).element.value).toBe('21')
      expect(wrapper.text()).not.toContain('shell.settings.taskLogRetention')
    }
  })
})
