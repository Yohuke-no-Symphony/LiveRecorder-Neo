// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import { defineComponent, h, nextTick } from 'vue'
import { AuthAPI } from '../src/apis/AuthAPI'
import { MediaAPI } from '../src/apis/MediaAPI'
import { i18n } from '../src/plugins/i18n'
import { useAuthStore } from '../src/stores/auth'
import { usePreferencesStore } from '../src/stores/preferences'
import type { UserProfile } from '../src/models/auth'
import type { ServerSettings } from '../src/models/settings'
import type { AppLanguage } from '../src/localization/types'
import AccountView from '../src/views/AccountView.vue'
import SettingsView from '../src/views/SettingsView.vue'
import AppBrand from '../src/components/common/AppBrand.vue'

const container = { template: '<div><slot /></div>' }
const select = defineComponent({
  props: ['modelValue', 'items', 'label', 'disabled', 'loading'],
  emits: ['update:modelValue'],
  setup(props, { emit })
  {
    return () => h('label', [props.label, h('select', {
      value: props.modelValue ?? '', 'aria-label': props.label, disabled: props.disabled || props.loading,
      onChange: (event: Event) => emit('update:modelValue', (event.target as HTMLSelectElement).value || null),
    }, (props.items as { title: string; value: string | null }[]).map(item => h('option', { value: item.value ?? '' }, item.title)))])
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
  VSelect: select, VBtn: button, VCard: container, VList: container, VListItem: container,
  VTabs: container, VTab: container, VSnackbar: snackbar,
  VAlert: { template: '<div role="alert"><slot /></div>' }, VProgressLinear: true, VIcon: true,
}
const mounted: VueWrapper[] = []
let pinia: Pinia

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

function user(language: AppLanguage | null): UserProfile
{
  return { id: 'user-1', email: 'user@example.test', displayName: '原始用户昵称', role: 'Admin', status: 'Active', language }
}

function settings(defaultLanguage: AppLanguage): ServerSettings
{
  return {
    id: 'server', defaultLanguage, storagePath: '/srv/recordings', timeZone: 'Asia/Shanghai', quality: 10000,
    recordDanmu: true, segmentBytes: 1024 ** 3, clipRetentionDays: 7, trashRetentionDays: 7,
    cacheLimitBytes: 1024 ** 3, cacheRetentionMinutes: 30, minimumFreeBytes: 1024 ** 3,
    mediaConcurrency: 2, playbackConcurrency: 2, monitorIntervalSeconds: 15, sessionGraceMinutes: 5,
    registrationEnabled: true, ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe',
    smtp: { host: '', port: 587, useSsl: true, checkCertificateRevocation: true, userName: '', password: '', fromAddress: '', fromName: '' },
    oauthProviders: [],
  }
}

beforeEach(() =>
{
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() })
  pinia = createPinia()
  setActivePinia(pinia)
  i18n.global.locale.value = 'zh-CN'
  vi.spyOn(AuthAPI, 'providers').mockResolvedValue([])
  vi.spyOn(AuthAPI, 'identities').mockResolvedValue([])
})

afterEach(() =>
{
  mounted.splice(0).forEach(wrapper => wrapper.unmount())
  disposePinia(pinia)
  vi.restoreAllMocks()
  i18n.global.locale.value = 'zh-CN'
  vi.unstubAllGlobals()
})

describe('账户语言设置', () =>
{
  it('仅在服务器确认保存后更新账户、选择器和生效语言', async () =>
  {
    const auth = useAuthStore()
    const preferences = usePreferencesStore()
    auth.user = user('zh-CN')
    const pending = deferred<UserProfile>()
    const save = vi.spyOn(AuthAPI, 'setLanguage').mockReturnValue(pending.promise)
    const wrapper = mount(AccountView, { global: { plugins: [pinia, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()

    await wrapper.get('select').setValue('ja-JP')
    expect(save).toHaveBeenCalledExactlyOnceWith('ja-JP')
    expect(auth.user?.language).toBe('zh-CN')
    expect(preferences.effectiveLanguage).toBe('zh-CN')
    expect(i18n.global.locale.value).toBe('zh-CN')
    expect(wrapper.get('select').element.value).toBe('zh-CN')
    expect(wrapper.get('select').attributes('disabled')).toBeDefined()

    pending.resolve(user('ja-JP'))
    await flushPromises()
    expect(auth.user?.language).toBe('ja-JP')
    expect(preferences.effectiveLanguage).toBe('ja-JP')
    expect(i18n.global.locale.value).toBe('ja-JP')
    expect(wrapper.get('select').element.value).toBe('ja-JP')
    expect(wrapper.get('select').attributes('disabled')).toBeUndefined()
    expect(wrapper.get('[role="status"]').text()).toBe('言語設定を保存しました')
  })

  it('保存失败时保留旧语言并允许重试', async () =>
  {
    const auth = useAuthStore()
    const preferences = usePreferencesStore()
    auth.user = user('zh-CN')
    const pending = deferred<UserProfile>()
    vi.spyOn(AuthAPI, 'setLanguage').mockReturnValue(pending.promise)
    const wrapper = mount(AccountView, { global: { plugins: [pinia, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()

    await wrapper.get('select').setValue('en-US')
    pending.reject(new Error('语言服务不可用'))
    await flushPromises()
    expect(auth.user?.language).toBe('zh-CN')
    expect(preferences.effectiveLanguage).toBe('zh-CN')
    expect(i18n.global.locale.value).toBe('zh-CN')
    expect(wrapper.get('select').element.value).toBe('zh-CN')
    expect(wrapper.get('select').attributes('disabled')).toBeUndefined()
    expect(wrapper.get('[role="alert"]').text()).toBe('语言服务不可用')
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
  })

  it('将跟随全局保存为 null，并立即采用管理员的默认语言', async () =>
  {
    const auth = useAuthStore()
    const preferences = usePreferencesStore()
    preferences.setDefaultLanguage('en-US')
    auth.user = user('zh-CN')
    const save = vi.spyOn(AuthAPI, 'setLanguage').mockResolvedValue(user(null))
    const wrapper = mount(AccountView, { global: { plugins: [pinia, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()

    await wrapper.get('select').setValue('')
    await flushPromises()
    expect(save).toHaveBeenCalledExactlyOnceWith(null)
    expect(auth.user?.language).toBeNull()
    expect(preferences.effectiveLanguage).toBe('en-US')
    expect(i18n.global.locale.value).toBe('en-US')
    expect(wrapper.get('select').element.value).toBe('')
  })
})

describe('管理员默认语言', () =>
{
  it('通过保存全局设置同步 preferences，并更新跟随全局的账户', async () =>
  {
    const auth = useAuthStore()
    const preferences = usePreferencesStore()
    auth.user = user(null)
    vi.spyOn(MediaAPI, 'settings').mockResolvedValueOnce(settings('zh-CN')).mockResolvedValueOnce(settings('ja-JP'))
    const pending = deferred<ServerSettings>()
    const save = vi.spyOn(MediaAPI, 'updateSettings').mockReturnValue(pending.promise)
    const wrapper = mount(SettingsView, { global: { plugins: [pinia, i18n], stubs } })
    mounted.push(wrapper)
    await flushPromises()

    expect([...wrapper.get('select').element.options].map(option => option.value)).toEqual(['zh-CN', 'en-US', 'ja-JP'])
    await wrapper.get('select').setValue('ja-JP')
    expect(save).not.toHaveBeenCalled()
    expect(preferences.defaultLanguage).toBe('zh-CN')
    await wrapper.get('header button').trigger('click')
    expect(save).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ defaultLanguage: 'ja-JP' }))
    expect(preferences.defaultLanguage).toBe('zh-CN')
    expect(i18n.global.locale.value).toBe('zh-CN')

    pending.resolve(settings('ja-JP'))
    await flushPromises()
    expect(preferences.defaultLanguage).toBe('ja-JP')
    expect(preferences.effectiveLanguage).toBe('ja-JP')
    expect(i18n.global.locale.value).toBe('ja-JP')
    expect(wrapper.get('h1').text()).toBe('設定')
    expect(wrapper.get('[role="status"]').text()).toBe('設定を保存しました')
  })
})

describe('网站品牌', () =>
{
  it('保持同一角色 LOGO，实时显示所选语言的网站名', async () =>
  {
    const wrapper = mount(AppBrand, { global: { plugins: [i18n] } })
    mounted.push(wrapper)
    const logo = wrapper.get('img').attributes('src')
    for (const [language, name] of [['zh-CN', '夜半录播'], ['en-US', 'Yohuke Live Recorder'], ['ja-JP', '夜更けのレコーダー']] as const)
    {
      i18n.global.locale.value = language
      await nextTick()
      expect(wrapper.text()).toBe(name)
      expect(wrapper.get('img').attributes('src')).toBe(logo)
    }
  })
})
