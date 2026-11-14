// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import { defineComponent, h, nextTick } from 'vue'
import { AuthAPI } from '../src/apis/AuthAPI'
import { MediaAPI } from '../src/apis/MediaAPI'
import { setCsrfToken } from '../src/apis/APIBase'
import { i18n } from '../src/plugins/i18n'
import { useAuthStore } from '../src/stores/auth'
import type { UserProfile } from '../src/models/auth'
import type { ServerSettings } from '../src/models/settings'
import AccountView from '../src/views/AccountView.vue'
import SettingsView from '../src/views/SettingsView.vue'
import SessionConfigurationDialog from '../src/components/library/SessionConfigurationDialog.vue'
import { SessionConfigurationAPI } from '../src/apis/SessionConfigurationAPI'
import type { SessionConfiguration } from '../src/models/sessionConfiguration'

const container = { template: '<div><slot /></div>' }
const textField = defineComponent({
  props: ['modelValue', 'label', 'hint', 'placeholder', 'disabled', 'readonly'],
  emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('label', [props.label, h('input', {
      value: props.modelValue, 'aria-label': props.label, placeholder: props.placeholder,
      disabled: props.disabled, readonly: props.readonly,
      onInput: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).value),
    }), props.hint])
  },
})
const toggle = defineComponent({
  props: ['modelValue', 'label', 'disabled'],
  emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('label', [props.label, h('input', {
      type: 'checkbox', checked: props.modelValue, 'aria-label': props.label, disabled: props.disabled,
      onChange: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).checked),
    })])
  },
})
const button = defineComponent({
  props: ['type', 'disabled', 'loading'],
  setup(props, { slots })
  {
    return () => h('button', { type: props.type ?? 'button', disabled: props.disabled || props.loading }, slots.default?.())
  },
})
const snackbar = defineComponent({
  props: ['modelValue'],
  setup(props, { slots })
  {
    return () => props.modelValue ? h('div', { role: 'status' }, slots.default?.()) : null
  },
})
const stubs = {
  VDialog: container, VBtn: button, VCard: container, VCardText: container, VList: container, VListItem: container,
  VTabs: container, VTab: container, VSnackbar: snackbar, VTextField: textField, VSwitch: toggle,
  VSelect: true, VAlert: { template: '<div role="alert"><slot /></div>' }, VProgressLinear: true, VIcon: true, VDivider: true,
}
const mounted: VueWrapper[] = []
let pinia: Pinia

function user(id = 'user-1'): UserProfile
{
  return { id, email: `${id}@example.test`, displayName: id, role: 'User', status: 'Active', permissions: [], language: null }
}

function settings(translationDanmuPattern: string): ServerSettings
{
  return {
    id: 'server', defaultLanguage: 'zh-CN', translationDanmuPattern, timeZone: 'Asia/Shanghai', quality: 10000,
    recordDanmu: true, segmentBytes: 1024 ** 3, recordingRetentionDays: 0, clipRetentionDays: 7, trashRetentionDays: 7,
    cacheLimitBytes: 1024 ** 3, cacheRetentionMinutes: 30, minimumFreeBytes: 1024 ** 3,
    mediaConcurrency: 2, playbackConcurrency: 2, monitorIntervalSeconds: 15, sessionGraceMinutes: 5,
    streamDisconnectTimeoutMinutes: 5, defaultUserPermissions: [], registrationEnabled: true,
    ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe', registrationApprovalRequired: true,
    registrationInvitationRequired: true, registrationEmailVerificationRequired: true,
    smtp: { host: '', port: 587, useSsl: true, checkCertificateRevocation: true, userName: '', password: '', fromAddress: '', fromName: '' },
    oauthProviders: [],
  }
}

function deferred<T>()
{
  let resolve!: (value: T) => void
  let reject!: (error: Error) => void
  const promise = new Promise<T>((resolvePromise, rejectPromise) =>
  {
    resolve = resolvePromise
    reject = rejectPromise
  })
  return { promise, resolve, reject }
}

async function openAccount(): Promise<VueWrapper>
{
  useAuthStore().user = user()
  const wrapper = mount(AccountView, { global: { plugins: [pinia, i18n], stubs } })
  mounted.push(wrapper)
  await flushPromises()
  return wrapper
}

async function openSettings(): Promise<VueWrapper>
{
  const wrapper = mount(SettingsView, { global: { plugins: [pinia, i18n], stubs } })
  mounted.push(wrapper)
  await flushPromises()
  return wrapper
}

beforeEach(() =>
{
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() })
  pinia = createPinia()
  setActivePinia(pinia)
  i18n.global.locale.value = 'zh-CN'
  vi.spyOn(AuthAPI, 'providers').mockResolvedValue([])
  vi.spyOn(AuthAPI, 'identities').mockResolvedValue([])
  vi.spyOn(MediaAPI, 'settingsPaths').mockResolvedValue({
    dataDirectory: '/srv/Data', recordsDirectory: '/srv/Data/records', cacheDirectory: '/srv/Data/cache',
    configFile: '/srv/settings.json', configuredDataDirectory: 'Data', configuredRecordsDirectory: 'Data/records', restartRequired: false,
  })
})

afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  disposePinia(pinia)
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  setCsrfToken('')
  i18n.global.locale.value = 'zh-CN'
})

describe('全局同传规则', () =>
{
  it('读取服务器的默认规则，显式保存 .NET 正则并在重开时保持', async () =>
  {
    let current = settings('^翻译：')
    vi.spyOn(MediaAPI, 'settings').mockImplementation(async () => structuredClone(current))
    const update = vi.spyOn(MediaAPI, 'updateSettings').mockImplementation(async value =>
    {
      current = JSON.parse(JSON.stringify(value)) as ServerSettings
      return current
    })
    const wrapper = await openSettings()
    const input = wrapper.get<HTMLInputElement>('input[aria-label="全局同传正则"]')
    expect(input.element.value).toBe('^翻译：')
    const pattern = '(?i)translation[:：] .+'
    await input.setValue(pattern)
    expect(update).not.toHaveBeenCalled()
    await wrapper.get('header button').trigger('click')
    await flushPromises()
    expect(update).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ translationDanmuPattern: pattern }))
    expect(wrapper.get('[role="status"]').text()).toBe('设置已保存')
    const reopened = await openSettings()
    expect(reopened.get<HTMLInputElement>('input[aria-label="全局同传正则"]').element.value).toBe(pattern)
  })

  it('服务端拒绝无效规则时保留编辑值并显示错误', async () =>
  {
    vi.spyOn(MediaAPI, 'settings').mockResolvedValue(settings('.*?【.*?】'))
    const update = vi.spyOn(MediaAPI, 'updateSettings').mockRejectedValue(new Error('同传正则无效'))
    const wrapper = await openSettings()
    await wrapper.get('input[aria-label="全局同传正则"]').setValue('[')
    await wrapper.get('header button').trigger('click')
    await flushPromises()
    expect(update).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ translationDanmuPattern: '[' }))
    expect(wrapper.get('[role="alert"]').text()).toBe('同传正则无效')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="全局同传正则"]').element.value).toBe('[')
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
  })
})

describe('录像共享同传配置', () =>
{
  async function openConfiguration(value: SessionConfiguration): Promise<VueWrapper>
  {
    vi.spyOn(SessionConfigurationAPI, 'get').mockResolvedValue(value)
    const wrapper = mount(SessionConfigurationDialog, { props: { sessionId: 'session-1', modelValue: true }, global: { plugins: [pinia, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()
    return wrapper
  }

  function saveButton(wrapper: VueWrapper)
  {
    return wrapper.findAll('button').find(button => button.text() === '保存')!
  }

  it('个人账户不再显示或提供同传规则配置', async () =>
  {
    const wrapper = await openAccount()
    expect(wrapper.text()).not.toContain('同传')
    expect('setTranslationDanmuPattern' in AuthAPI).toBe(false)
    expect('setTranslationDanmuPattern' in useAuthStore()).toBe(false)
  })

  it('保存显式覆盖携带版本，成功前保持窗口与已保存值', async () =>
  {
    const wrapper = await openConfiguration({ translationDanmuPatternOverride: null, effectiveTranslationDanmuPattern: '^全局', revision: 4 })
    const input = wrapper.get<HTMLInputElement>('input[aria-label="本录像同传正则"]')
    expect(input.element.disabled).toBe(true)
    const pending = deferred<SessionConfiguration>()
    const save = vi.spyOn(SessionConfigurationAPI, 'update').mockReturnValue(pending.promise)
    await wrapper.get('input[type="checkbox"]').setValue(false)
    await input.setValue('(?i)^译')
    await saveButton(wrapper).trigger('click')
    expect(save).toHaveBeenCalledExactlyOnceWith('session-1', '(?i)^译', 4)
    expect(wrapper.emitted('saved')).toBeUndefined()
    expect(input.element.disabled).toBe(true)
    const saved = { translationDanmuPatternOverride: '(?i)^译', effectiveTranslationDanmuPattern: '(?i)^译', revision: 5 }
    pending.resolve(saved)
    await flushPromises()
    expect(wrapper.emitted('saved')).toEqual([[saved]])
    expect(wrapper.emitted('update:modelValue')).toEqual([[false]])
  })

  it('并发保存冲突保留草稿，重新读取后用新版本恢复跟随全局', async () =>
  {
    const wrapper = await openConfiguration({ translationDanmuPatternOverride: '^旧', effectiveTranslationDanmuPattern: '^旧', revision: 2 })
    const save = vi.spyOn(SessionConfigurationAPI, 'update').mockRejectedValueOnce(new Error('配置已由其他人修改')).mockResolvedValueOnce({ translationDanmuPatternOverride: null, effectiveTranslationDanmuPattern: '^全局', revision: 4 })
    const input = wrapper.get<HTMLInputElement>('input[aria-label="本录像同传正则"]')
    await input.setValue('^草稿')
    await saveButton(wrapper).trigger('click')
    await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toBe('配置已由其他人修改')
    expect(input.element.value).toBe('^草稿')
    vi.mocked(SessionConfigurationAPI.get).mockResolvedValue({ translationDanmuPatternOverride: '^他人', effectiveTranslationDanmuPattern: '^他人', revision: 3 })
    await wrapper.findAll('button').find(button => button.text() === '重新读取配置')!.trigger('click')
    await flushPromises()
    await wrapper.get('input[type="checkbox"]').setValue(true)
    await saveButton(wrapper).trigger('click')
    await flushPromises()
    expect(save).toHaveBeenLastCalledWith('session-1', null, 3)
    expect(wrapper.emitted('saved')).toHaveLength(1)
  })

  it('切换录像后丢弃旧请求响应', async () =>
  {
    const old = deferred<SessionConfiguration>()
    vi.spyOn(SessionConfigurationAPI, 'get').mockReturnValueOnce(old.promise).mockResolvedValue({ translationDanmuPatternOverride: '^新录像', effectiveTranslationDanmuPattern: '^新录像', revision: 7 })
    const wrapper = mount(SessionConfigurationDialog, { props: { sessionId: 'old', modelValue: true }, global: { plugins: [pinia, i18n], stubs } })
    mounted.push(wrapper)
    await wrapper.setProps({ sessionId: 'new' })
    await flushPromises()
    old.resolve({ translationDanmuPatternOverride: '^旧录像', effectiveTranslationDanmuPattern: '^旧录像', revision: 1 })
    await flushPromises()
    expect(wrapper.get<HTMLInputElement>('input[aria-label="本录像同传正则"]').element.value).toBe('^新录像')
  })

  it('配置 API 使用录像路径、版本和 CSRF', async () =>
  {
    const fetcher = vi.fn(async () => new Response(JSON.stringify({ translationDanmuPatternOverride: '^录像', effectiveTranslationDanmuPattern: '^录像', revision: 2 }), { headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetcher)
    setCsrfToken('current-csrf')
    const result = await SessionConfigurationAPI.update('session /中文', '^录像', 1)
    const [url, request] = fetcher.mock.calls[0]! as unknown as [string, RequestInit]
    expect(String(url)).toContain('/api/sessions/session%20%2F%E4%B8%AD%E6%96%87/configuration')
    expect(request).toMatchObject({ method: 'PUT', credentials: 'include', body: JSON.stringify({ translationDanmuPatternOverride: '^录像', expectedRevision: 1 }) })
    expect(new Headers(request.headers).get('X-CSRF-TOKEN')).toBe('current-csrf')
    expect(result.revision).toBe(2)
  })

  it('保存过程中切换录像不会阻塞新录像，迟到保存响应不关闭新窗口', async () =>
  {
    const wrapper = await openConfiguration({ translationDanmuPatternOverride: '^旧', effectiveTranslationDanmuPattern: '^旧', revision: 1 })
    const old = deferred<SessionConfiguration>()
    vi.spyOn(SessionConfigurationAPI, 'update').mockReturnValue(old.promise)
    await saveButton(wrapper).trigger('click')
    vi.mocked(SessionConfigurationAPI.get).mockResolvedValue({ translationDanmuPatternOverride: '^新', effectiveTranslationDanmuPattern: '^新', revision: 4 })
    await wrapper.setProps({ sessionId: 'new' })
    await flushPromises()
    expect(saveButton(wrapper).attributes('disabled')).toBeUndefined()
    old.resolve({ translationDanmuPatternOverride: '^旧', effectiveTranslationDanmuPattern: '^旧', revision: 1 })
    await flushPromises()
    expect(wrapper.emitted('saved')).toBeUndefined()
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
    expect(wrapper.get<HTMLInputElement>('input[aria-label="本录像同传正则"]').element.value).toBe('^新')
  })
})

describe('同传设置三语文案', () =>
{
  it.each(['zh-CN', 'en-US', 'ja-JP'] as const)('%s 的全局与录像设置文案已完整替换个人设置', async language =>
  {
    i18n.global.locale.value = language
    await nextTick()
    expect(i18n.global.te('shell.recordingConfiguration.title', language)).toBe(true)
    expect(i18n.global.te('shell.recordingConfiguration.groupHint', language)).toBe(true)
    expect(i18n.global.te('shell.account.personalTranslationPattern', language)).toBe(false)
  })
})
