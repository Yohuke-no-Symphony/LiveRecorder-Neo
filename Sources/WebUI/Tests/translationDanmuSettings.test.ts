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
  VBtn: button, VCard: container, VCardText: container, VList: container, VListItem: container,
  VTabs: container, VTab: container, VSnackbar: snackbar, VTextField: textField, VSwitch: toggle,
  VSelect: true, VAlert: { template: '<div role="alert"><slot /></div>' }, VProgressLinear: true, VIcon: true, VDivider: true,
}
const mounted: VueWrapper[] = []
let pinia: Pinia

function user(translationDanmuPattern: string | null = null, id = 'user-1'): UserProfile
{
  return { id, email: `${id}@example.test`, displayName: id, role: 'User', status: 'Active', permissions: [], language: null, translationDanmuPattern }
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

async function openAccount(pattern: string | null = null): Promise<VueWrapper>
{
  useAuthStore().user = user(pattern)
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
    const input = wrapper.get<HTMLInputElement>('input[aria-label="默认同传正则"]')
    expect(input.element.value).toBe('^翻译：')
    const pattern = '(?i)translation[:：] .+'
    await input.setValue(pattern)
    expect(update).not.toHaveBeenCalled()
    await wrapper.get('header button').trigger('click')
    await flushPromises()
    expect(update).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ translationDanmuPattern: pattern }))
    expect(wrapper.get('[role="status"]').text()).toBe('设置已保存')
    const reopened = await openSettings()
    expect(reopened.get<HTMLInputElement>('input[aria-label="默认同传正则"]').element.value).toBe(pattern)
  })

  it('服务端拒绝无效规则时保留编辑值并显示错误', async () =>
  {
    vi.spyOn(MediaAPI, 'settings').mockResolvedValue(settings('.*?【.*?】'))
    const update = vi.spyOn(MediaAPI, 'updateSettings').mockRejectedValue(new Error('同传正则无效'))
    const wrapper = await openSettings()
    await wrapper.get('input[aria-label="默认同传正则"]').setValue('[')
    await wrapper.get('header button').trigger('click')
    await flushPromises()
    expect(update).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ translationDanmuPattern: '[' }))
    expect(wrapper.get('[role="alert"]').text()).toBe('同传正则无效')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="默认同传正则"]').element.value).toBe('[')
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
  })
})

describe('账户同传规则', () =>
{
  it('默认跟随全局，输入个人规则后等待保存成功才更新账户', async () =>
  {
    const wrapper = await openAccount()
    const inherit = wrapper.get<HTMLInputElement>('input[aria-label="跟随全局默认"]')
    const input = wrapper.get<HTMLInputElement>('input[aria-label="个人同传正则"]')
    expect(inherit.element.checked).toBe(true)
    expect(input.element.disabled).toBe(true)
    expect(input.element.value).toBe('')
    const pending = deferred<UserProfile>()
    const save = vi.spyOn(AuthAPI, 'setTranslationDanmuPattern').mockReturnValue(pending.promise)
    await inherit.setValue(false)
    const pattern = '(?i)^译[:：] .+'
    await input.setValue(pattern)
    expect(save).not.toHaveBeenCalled()
    expect(useAuthStore().user?.translationDanmuPattern).toBeNull()
    await wrapper.get('form').trigger('submit')
    expect(save).toHaveBeenCalledExactlyOnceWith(pattern)
    expect(input.element.disabled).toBe(true)
    expect(useAuthStore().user?.translationDanmuPattern).toBeNull()
    pending.resolve(user(pattern))
    await flushPromises()
    expect(useAuthStore().user?.translationDanmuPattern).toBe(pattern)
    expect(input.element.disabled).toBe(false)
    expect(wrapper.get('[role="status"]').text()).toBe('同传设置已保存')
  })

  it('保存失败保留个人草稿和已生效规则，允许再次保存', async () =>
  {
    const wrapper = await openAccount('^旧规则')
    const input = wrapper.get<HTMLInputElement>('input[aria-label="个人同传正则"]')
    const save = vi.spyOn(AuthAPI, 'setTranslationDanmuPattern').mockRejectedValueOnce(new Error('同传正则无效')).mockResolvedValueOnce(user('^新规则'))
    await input.setValue('[')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(input.element.value).toBe('[')
    expect(input.element.disabled).toBe(false)
    expect(useAuthStore().user?.translationDanmuPattern).toBe('^旧规则')
    expect(wrapper.get('[role="alert"]').text()).toBe('同传正则无效')
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
    await input.setValue('^新规则')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(save).toHaveBeenNthCalledWith(2, '^新规则')
    expect(useAuthStore().user?.translationDanmuPattern).toBe('^新规则')
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
  })

  it('恢复跟随全局时提交 null，账号切换清除上一账号的未保存草稿', async () =>
  {
    const wrapper = await openAccount('^个人')
    const input = wrapper.get<HTMLInputElement>('input[aria-label="个人同传正则"]')
    const save = vi.spyOn(AuthAPI, 'setTranslationDanmuPattern').mockResolvedValue(user(null))
    await wrapper.get('input[aria-label="跟随全局默认"]').setValue(true)
    expect(save).not.toHaveBeenCalled()
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(save).toHaveBeenCalledExactlyOnceWith(null)
    expect(useAuthStore().user?.translationDanmuPattern).toBeNull()
    await wrapper.get('input[aria-label="跟随全局默认"]').setValue(false)
    await input.setValue('^未保存')
    useAuthStore().user = user('^另一账号', 'user-2')
    await nextTick()
    expect(input.element.value).toBe('^另一账号')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="跟随全局默认"]').element.checked).toBe(false)
  })

  it('更新同一账号的语言不会覆盖尚未保存的同传草稿', async () =>
  {
    const wrapper = await openAccount('^个人')
    const input = wrapper.get<HTMLInputElement>('input[aria-label="个人同传正则"]')
    await input.setValue('^未保存')
    useAuthStore().user = { ...user('^个人'), language: 'en-US' }
    await nextTick()
    expect(input.element.value).toBe('^未保存')
  })

  it('账号切换后忽略上一账号尚未完成的保存响应', async () =>
  {
    const wrapper = await openAccount('^个人')
    const pending = deferred<UserProfile>()
    vi.spyOn(AuthAPI, 'setTranslationDanmuPattern').mockReturnValue(pending.promise)
    await wrapper.get('input[aria-label="个人同传正则"]').setValue('^保存中')
    await wrapper.get('form').trigger('submit')
    useAuthStore().user = user('^另一账号', 'user-2')
    await nextTick()
    pending.resolve(user('^保存中'))
    await flushPromises()
    expect(useAuthStore().user?.id).toBe('user-2')
    expect(useAuthStore().user?.translationDanmuPattern).toBe('^另一账号')
    expect(wrapper.get<HTMLInputElement>('input[aria-label="个人同传正则"]').element.value).toBe('^另一账号')
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
  })

  it('API 只提交规则并沿用当前身份与 CSRF，不接受目标账号 ID', async () =>
  {
    const fetcher = vi.fn(async () => new Response(JSON.stringify(user('^个人')), { headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetcher)
    setCsrfToken('current-csrf')
    const result = await AuthAPI.setTranslationDanmuPattern('^个人')
    const [url, request] = fetcher.mock.calls[0]! as unknown as [string, RequestInit]
    expect(String(url)).toMatch(/\/api\/auth\/translation-danmu-pattern$/)
    expect(request).toMatchObject({ method: 'PUT', credentials: 'include', body: JSON.stringify({ pattern: '^个人' }) })
    expect(new Headers(request.headers).get('X-CSRF-TOKEN')).toBe('current-csrf')
    expect(result.translationDanmuPattern).toBe('^个人')
  })
})

describe('同传设置三语文案', () =>
{
  it.each([
    ['zh-CN', '默认同传正则', '个人同传正则', '跟随全局默认', '保存同传设置'],
    ['en-US', 'Default translation pattern', 'Personal translation pattern', 'Use global default', 'Save translation settings'],
    ['ja-JP', '既定の同時通訳の正規表現', '個人の同時通訳の正規表現', '全体の既定値を使用', '同時通訳設定を保存'],
  ] as const)('%s 展示全局与个人设置', async (language, globalLabel, personalLabel, inheritLabel, saveLabel) =>
  {
    vi.spyOn(MediaAPI, 'settings').mockResolvedValue(settings('^译'))
    const admin = await openSettings()
    const account = await openAccount()
    i18n.global.locale.value = language
    await nextTick()
    expect(admin.get<HTMLInputElement>(`input[aria-label="${globalLabel}"]`).element.value).toBe('^译')
    expect(account.get<HTMLInputElement>(`input[aria-label="${personalLabel}"]`).element.disabled).toBe(true)
    expect(account.get<HTMLInputElement>(`input[aria-label="${inheritLabel}"]`).element.checked).toBe(true)
    expect(account.get('form button').text()).toBe(saveLabel)
  })
})
