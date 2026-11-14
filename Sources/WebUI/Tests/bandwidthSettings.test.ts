// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import { defineComponent, h, nextTick } from 'vue'
import { AuthAPI } from '../src/apis/AuthAPI'
import { MediaAPI } from '../src/apis/MediaAPI'
import { i18n } from '../src/plugins/i18n'
import { BYTES_PER_MIB, BYTES_PER_MEGABIT, bandwidthInputBytes, isValidBandwidthSettings, normalizeBandwidthSettings } from '../src/models/bandwidth'
import type { ServerSettings } from '../src/models/settings'
import SettingsView from '../src/views/SettingsView.vue'

vi.mock('../src/apis/MediaAPI', () => ({
  MediaAPI: {
    settings: vi.fn(), updateSettings: vi.fn(),
    settingsPaths: vi.fn(async () => ({ dataDirectory: '/srv/Data', recordsDirectory: '/srv/records', cacheDirectory: '/srv/Data/cache', configFile: '/srv/settings.json', configuredDataDirectory: 'Data', configuredRecordsDirectory: 'records', restartRequired: false })),
  },
}))

const container = { template: '<div><slot /></div>' }
const tabs = defineComponent({
  props: ['modelValue'],
  emits: ['update:modelValue'],
  setup(_, { slots })
  {
    return () => h('div', slots.default?.())
  },
})
const field = defineComponent({
  props: ['modelValue', 'label', 'type', 'disabled', 'min', 'max', 'step', 'errorMessages'],
  emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('input', {
      value: props.modelValue, 'aria-label': props.label, type: props.type ?? 'text', disabled: props.disabled,
      min: props.min, max: props.max, step: props.step, 'data-error': props.errorMessages || undefined,
      onInput: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).value),
    })
  },
})
const toggle = defineComponent({
  props: ['modelValue', 'label', 'disabled'],
  emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('input', {
      type: 'checkbox', checked: props.modelValue, 'aria-label': props.label, disabled: props.disabled,
      onChange: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).checked),
    })
  },
})
const stubs = {
  VTabs: tabs, VTab: container, VTextField: field, VSwitch: toggle, VSelect: true, VProgressLinear: true,
  VBtn: { props: ['disabled', 'loading'], template: '<button :disabled="disabled || loading"><slot /></button>' },
  VAlert: { template: '<div role="alert"><slot /></div>' },
  VSnackbar: { props: ['modelValue'], template: '<div v-if="modelValue" role="status"><slot /></div>' },
  ContentCard: container, DataDirectorySettings: true, LanguageSelect: true,
}
const USER_LIMIT = 'input[aria-label="每账号下载上限（MiB/s）"]'
const TOTAL = 'input[aria-label="总可用带宽（Mbps）"]'
const ENABLED = 'input[aria-label="启用智能限速"]'
const GUARANTEE = 'input[aria-label="下载保底比例（%）"]'
const mounted: VueWrapper[] = []
let pinia: Pinia
let current: ServerSettings

function serverSettings(): ServerSettings
{
  return {
    id: 'server', defaultLanguage: 'zh-CN', translationDanmuPattern: '.*?【.*?】', timeZone: 'Asia/Shanghai', quality: 10000,
    recordDanmu: true, segmentBytes: 1024 ** 3, recordingRetentionDays: 0, clipRetentionDays: 7, trashRetentionDays: 7,
    cacheLimitBytes: 1024 ** 3, cacheRetentionMinutes: 30, minimumFreeBytes: 1024 ** 3,
    mediaConcurrency: 2, playbackConcurrency: 2, monitorIntervalSeconds: 15, sessionGraceMinutes: 5,
    streamDisconnectTimeoutMinutes: 5, defaultUserPermissions: [], registrationEnabled: true,
    registrationApprovalRequired: true, registrationInvitationRequired: true, registrationEmailVerificationRequired: true,
    ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe', oauthProviders: [],
    smtp: { host: '', port: 587, useSsl: true, checkCertificateRevocation: true, userName: '', password: '', fromAddress: '', fromName: '' },
  }
}

async function openBandwidth(): Promise<VueWrapper>
{
  const wrapper = mount(SettingsView, { global: { plugins: [pinia, i18n], stubs } })
  mounted.push(wrapper)
  await flushPromises()
  expect(wrapper.text()).toContain('带宽')
  wrapper.findComponent(tabs).vm.$emit('update:modelValue', 'bandwidth')
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
  vi.clearAllMocks()
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() })
  pinia = createPinia()
  setActivePinia(pinia)
  i18n.global.locale.value = 'zh-CN'
  current = serverSettings()
  vi.mocked(MediaAPI.settings).mockImplementation(async () => structuredClone(current))
  vi.mocked(MediaAPI.updateSettings).mockImplementation(async value =>
  {
    current = JSON.parse(JSON.stringify(value)) as ServerSettings
    return structuredClone(current)
  })
  vi.spyOn(AuthAPI, 'providers').mockResolvedValue([])
})

afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  disposePinia(pinia)
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  i18n.global.locale.value = 'zh-CN'
})

describe('带宽设置', () =>
{
  it('旧配置打开独立页签后默认不限速，并只在保存时提交新增设置', async () =>
  {
    const wrapper = await openBandwidth()
    expect(wrapper.get<HTMLInputElement>(USER_LIMIT).element.value).toBe('0')
    expect(wrapper.get<HTMLInputElement>(ENABLED).element.checked).toBe(false)
    expect(wrapper.get<HTMLInputElement>(TOTAL).element.value).toBe('0')
    expect(wrapper.get<HTMLInputElement>(TOTAL).element.disabled).toBe(true)
    expect(wrapper.get<HTMLInputElement>(GUARANTEE).element.value).toBe('10')
    expect(wrapper.get<HTMLInputElement>(GUARANTEE).element.disabled).toBe(true)
    expect(wrapper.get<HTMLInputElement>(USER_LIMIT).element.disabled).toBe(false)
    expect(MediaAPI.updateSettings).not.toHaveBeenCalled()
    await save(wrapper)
    expect(current.bandwidth).toEqual(normalizeBandwidthSettings())
    expect(wrapper.get('[role="status"]').text()).toBe('设置已保存')
  })

  it('换算两种单位、保存并回显，编辑小数时不改变用户输入', async () =>
  {
    const wrapper = await openBandwidth()
    await wrapper.get(USER_LIMIT).setValue('1.1')
    expect(wrapper.get<HTMLInputElement>(USER_LIMIT).element.value).toBe('1.1')
    await wrapper.get(USER_LIMIT).setValue('1.25')
    await wrapper.get(ENABLED).setValue(true)
    await wrapper.get(TOTAL).setValue('100.5')
    await wrapper.get(GUARANTEE).setValue('25')
    expect(MediaAPI.updateSettings).not.toHaveBeenCalled()
    await save(wrapper)
    expect(current.bandwidth).toEqual({ userDownloadLimitBytesPerSecond: 1310720, smartLimitEnabled: true, totalBytesPerSecond: 12562500, downloadGuaranteedPercent: 25 })
    expect(wrapper.get<HTMLInputElement>(USER_LIMIT).element.value).toBe('1.25')
    expect(wrapper.get<HTMLInputElement>(TOTAL).element.value).toBe('100.5')
    expect(wrapper.get<HTMLInputElement>(GUARANTEE).element.value).toBe('25')
    const reopened = await openBandwidth()
    expect(reopened.get<HTMLInputElement>(USER_LIMIT).element.value).toBe('1.25')
    expect(reopened.get<HTMLInputElement>(TOTAL).element.value).toBe('100.5')
  })

  it('关闭智能限速保留总量与保底，用户上限仍可保存；保存期间所有输入禁用', async () =>
  {
    current.bandwidth = normalizeBandwidthSettings({ smartLimitEnabled: true, totalBytesPerSecond: 12500000, downloadGuaranteedPercent: 30 })
    const wrapper = await openBandwidth()
    await wrapper.get(ENABLED).setValue(false)
    await wrapper.get(USER_LIMIT).setValue('2')
    expect(wrapper.get<HTMLInputElement>(TOTAL).element.disabled).toBe(true)
    expect(wrapper.get<HTMLInputElement>(GUARANTEE).element.disabled).toBe(true)
    await save(wrapper)
    expect(current.bandwidth).toEqual({ userDownloadLimitBytesPerSecond: 2097152, smartLimitEnabled: false, totalBytesPerSecond: 12500000, downloadGuaranteedPercent: 30 })
    let completeSave!: (value: ServerSettings) => void
    vi.mocked(MediaAPI.updateSettings).mockReturnValueOnce(new Promise(resolve => { completeSave = resolve }))
    await wrapper.get('header button').trigger('click')
    expect(wrapper.findAll('input').every(input => (input.element as HTMLInputElement).disabled)).toBe(true)
    completeSave(structuredClone(current))
    await flushPromises()
  })

  it.each([
    [USER_LIMIT, '', false], [USER_LIMIT, '-1', false], [USER_LIMIT, '0.00000001', false],
    [USER_LIMIT, '10000000000', false], [TOTAL, '0', true], [TOTAL, '', true],
    [GUARANTEE, '-1', true], [GUARANTEE, '101', true], [GUARANTEE, '1.5', true], [GUARANTEE, '', true],
  ])('拒绝无效输入 %s = %s，保留编辑值且不保存', async (selector, value, smartEnabled) =>
  {
    current.bandwidth = normalizeBandwidthSettings({ smartLimitEnabled: smartEnabled, totalBytesPerSecond: 12500000 })
    const wrapper = await openBandwidth()
    await wrapper.get(selector).setValue(value)
    await save(wrapper)
    expect(MediaAPI.updateSettings).not.toHaveBeenCalled()
    expect(wrapper.get('[role="alert"]').text()).toContain('请检查带宽设置')
    expect(wrapper.get<HTMLInputElement>(selector).element.value).toBe(value)
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
  })

  it('保存失败保留编辑数值，重新提交成功后再读取服务器设置', async () =>
  {
    const wrapper = await openBandwidth()
    await wrapper.get(USER_LIMIT).setValue('3.5')
    vi.mocked(MediaAPI.updateSettings).mockRejectedValueOnce(new Error('配置文件不可写'))
    await save(wrapper)
    expect(wrapper.get('[role="alert"]').text()).toBe('配置文件不可写')
    expect(wrapper.get<HTMLInputElement>(USER_LIMIT).element.value).toBe('3.5')
    expect(MediaAPI.settings).toHaveBeenCalledTimes(1)
    await save(wrapper)
    expect(current.bandwidth?.userDownloadLimitBytesPerSecond).toBe(3670016)
    expect(MediaAPI.settings).toHaveBeenCalledTimes(2)
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
  })

  it.each([
    ['zh-CN', '每账号下载上限（MiB/s）', '总可用带宽（Mbps）'],
    ['en-US', 'Download limit per account (MiB/s)', 'Total available bandwidth (Mbps)'],
    ['ja-JP', 'アカウントごとのダウンロード上限（MiB/s）', '利用可能な総帯域幅（Mbps）'],
  ] as const)('%s 显示带单位的字段标签', async (language, userLabel, totalLabel) =>
  {
    const wrapper = await openBandwidth()
    i18n.global.locale.value = language
    await nextTick()
    expect(wrapper.find(`input[aria-label="${userLabel}"]`).exists()).toBe(true)
    expect(wrapper.find(`input[aria-label="${totalLabel}"]`).exists()).toBe(true)
    expect(wrapper.text()).not.toContain('bandwidthSettings.')
  })

  it('保留配置的0与100保底，拒绝非安全整数、非有限速率和小于1字节的正输入', () =>
  {
    expect(normalizeBandwidthSettings(null)).toEqual({ userDownloadLimitBytesPerSecond: 0, smartLimitEnabled: false, totalBytesPerSecond: 0, downloadGuaranteedPercent: 10 })
    for (const percent of [0, 100])
    {
      const settings = normalizeBandwidthSettings({ downloadGuaranteedPercent: percent })
      expect(settings.downloadGuaranteedPercent).toBe(percent)
      expect(isValidBandwidthSettings(settings)).toBe(true)
    }
    for (const rate of [-1, 0.5, Number.NaN, Number.POSITIVE_INFINITY, Number.MAX_SAFE_INTEGER + 1])
    {
      expect(isValidBandwidthSettings(normalizeBandwidthSettings({ userDownloadLimitBytesPerSecond: rate }))).toBe(false)
      expect(isValidBandwidthSettings(normalizeBandwidthSettings({ totalBytesPerSecond: rate }))).toBe(false)
    }
    expect(bandwidthInputBytes('1.5', BYTES_PER_MIB)).toBe(1572864)
    expect(bandwidthInputBytes('100', BYTES_PER_MEGABIT)).toBe(12500000)
    expect(bandwidthInputBytes('', BYTES_PER_MIB)).toBeNaN()
    expect(bandwidthInputBytes(null, BYTES_PER_MIB)).toBeNaN()
    expect(bandwidthInputBytes('-0.00000001', BYTES_PER_MIB)).toBeNaN()
    expect(bandwidthInputBytes('0.00000001', BYTES_PER_MIB)).toBeNaN()
    expect(isValidBandwidthSettings(normalizeBandwidthSettings({ smartLimitEnabled: true }))).toBe(false)
  })
})
