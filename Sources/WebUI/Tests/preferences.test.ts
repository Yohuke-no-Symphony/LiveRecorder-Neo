// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import { usePreferencesStore } from '../src/stores/preferences'
import { useAuthStore } from '../src/stores/auth'
import { AuthAPI } from '../src/apis/AuthAPI'
import { i18n } from '../src/plugins/i18n'
import { LANGUAGE_STORAGE_KEY } from '../src/localization/languages'
import type { UserProfile } from '../src/models/auth'

const instances: Pinia[] = []

function newPinia(): void
{
  const pinia = createPinia()
  instances.push(pinia)
  setActivePinia(pinia)
}

function user(id: string, language: UserProfile['language'] = null): UserProfile
{
  return { id, email: `${id}@example.test`, displayName: id, role: 'User', status: 'Active', permissions: [], language }
}

beforeEach(() =>
{
  const values = new Map<string, string>()
  const storage: Storage = {
    get length() { return values.size },
    clear: () => values.clear(),
    getItem: key => values.get(key) ?? null,
    key: index => [...values.keys()][index] ?? null,
    removeItem: key => { values.delete(key) },
    setItem: (key, value) => { values.set(key, value) },
  }
  vi.stubGlobal('localStorage', storage)
  i18n.global.locale.value = 'zh-CN'
  newPinia()
})

afterEach(() =>
{
  instances.splice(0).forEach(pinia => disposePinia(pinia))
  vi.restoreAllMocks()
  localStorage.clear()
  vi.unstubAllGlobals()
  i18n.global.locale.value = 'zh-CN'
})

describe('界面语言优先级与隔离', () =>
{
  it('用户偏好优先于全局默认，未选择的登录用户继承全局默认', () =>
  {
    const preferences = usePreferencesStore()
    preferences.setDefaultLanguage('ja-JP')
    preferences.setGuestLanguage('en-US')
    expect(preferences.effectiveLanguage).toBe('en-US')

    preferences.setSession(user('first'))
    expect(preferences.effectiveLanguage).toBe('ja-JP')
    expect(i18n.global.locale.value).toBe('ja-JP')
    preferences.setSession(user('first', 'en-US'))
    preferences.setDefaultLanguage('zh-CN')
    expect(preferences.effectiveLanguage).toBe('en-US')
    preferences.setSession(user('first', null))
    expect(preferences.effectiveLanguage).toBe('zh-CN')
  })

  it('账户切换不会继承上一账户或访客的语言，退出后恢复访客选择', async () =>
  {
    const preferences = usePreferencesStore()
    preferences.setDefaultLanguage('en-US')
    preferences.setGuestLanguage('ja-JP')
    vi.spyOn(AuthAPI, 'login')
      .mockResolvedValueOnce({ user: user('first', 'zh-CN'), csrfToken: 'first-csrf' })
      .mockResolvedValueOnce({ user: user('second', null), csrfToken: 'second-csrf' })
    vi.spyOn(AuthAPI, 'logout').mockResolvedValue(undefined)
    vi.spyOn(AuthAPI, 'me').mockResolvedValue({ user: null, csrfToken: 'guest-csrf' })
    const auth = useAuthStore()
    await auth.login('first@example.test', 'password')
    expect(i18n.global.locale.value).toBe('zh-CN')
    await auth.login('second@example.test', 'password')
    expect(i18n.global.locale.value).toBe('en-US')
    expect(preferences.userLanguage).toBeNull()
    await auth.logout()
    expect(i18n.global.locale.value).toBe('ja-JP')
    expect(preferences.signedIn).toBe(false)
    expect(localStorage.getItem(LANGUAGE_STORAGE_KEY)).toBe('ja-JP')
  })

  it('账户保存与清除偏好同步当前界面，访客存储保持独立', async () =>
  {
    const preferences = usePreferencesStore()
    preferences.setDefaultLanguage('zh-CN')
    preferences.setGuestLanguage('en-US')
    const auth = useAuthStore()
    auth.user = user('first')
    const save = vi.spyOn(AuthAPI, 'setLanguage')
      .mockResolvedValueOnce(user('first', 'ja-JP'))
      .mockResolvedValueOnce(user('first', null))

    await auth.setLanguage('ja-JP')
    expect(save).toHaveBeenLastCalledWith('ja-JP')
    expect(preferences.effectiveLanguage).toBe('ja-JP')
    expect(localStorage.getItem(LANGUAGE_STORAGE_KEY)).toBe('en-US')
    await auth.setLanguage(null)
    expect(save).toHaveBeenLastCalledWith(null)
    expect(preferences.effectiveLanguage).toBe('zh-CN')
    expect(localStorage.getItem(LANGUAGE_STORAGE_KEY)).toBe('en-US')
  })

  it('保存失败保留原账户偏好和界面语言', async () =>
  {
    const preferences = usePreferencesStore()
    const auth = useAuthStore()
    auth.user = user('first', 'en-US')
    vi.spyOn(AuthAPI, 'setLanguage').mockRejectedValue(new Error('unavailable'))
    await expect(auth.setLanguage('ja-JP')).rejects.toThrow('unavailable')
    expect(auth.user?.language).toBe('en-US')
    expect(preferences.effectiveLanguage).toBe('en-US')
    expect(i18n.global.locale.value).toBe('en-US')
  })

  it('从公开设置刷新全局默认不会覆盖明确的账户偏好', async () =>
  {
    const preferences = usePreferencesStore()
    preferences.setSession(user('first', 'en-US'))
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({ defaultLanguage: 'ja-JP' })))
    vi.stubGlobal('fetch', fetcher)
    await preferences.refreshDefaults()
    expect(String(fetcher.mock.calls[0]?.[0])).toMatch(/\/api\/ui-settings$/)
    expect(preferences.defaultLanguage).toBe('ja-JP')
    expect(preferences.effectiveLanguage).toBe('en-US')
    preferences.setSession(user('first', null))
    expect(preferences.effectiveLanguage).toBe('ja-JP')
  })
})

describe('访客语言保存与兼容', () =>
{
  it('刷新后恢复访客选择，选择跟随默认只清除语言键', () =>
  {
    const preferences = usePreferencesStore()
    preferences.setGuestLanguage('ja-JP')
    localStorage.setItem('lr.theme', 'dark')
    newPinia()
    const reloaded = usePreferencesStore()
    expect(reloaded.effectiveLanguage).toBe('ja-JP')
    reloaded.setDefaultLanguage('en-US')
    reloaded.setGuestLanguage(null)
    expect(reloaded.effectiveLanguage).toBe('en-US')
    expect(localStorage.getItem(LANGUAGE_STORAGE_KEY)).toBeNull()
    expect(localStorage.getItem('lr.theme')).toBe('dark')
  })

  it.each(['fr-FR', 'en-us', 'ja', 'null', '<script>'])('非法或历史访客值 %s 回退全局默认', stored =>
  {
    localStorage.setItem(LANGUAGE_STORAGE_KEY, stored)
    const preferences = usePreferencesStore()
    preferences.setDefaultLanguage('ja-JP')
    expect(preferences.guestLanguage).toBeNull()
    expect(preferences.effectiveLanguage).toBe('ja-JP')
    expect(i18n.global.locale.value).toBe('ja-JP')
    preferences.setDefaultLanguage('unsupported')
    expect(preferences.effectiveLanguage).toBe('zh-CN')
  })

  it('浏览器禁止本地存储时仍可在当前会话切换语言', () =>
  {
    vi.spyOn(localStorage, 'getItem').mockImplementation(() => { throw new DOMException('blocked', 'SecurityError') })
    vi.spyOn(localStorage, 'setItem').mockImplementation(() => { throw new DOMException('blocked', 'SecurityError') })
    const preferences = usePreferencesStore()
    expect(preferences.effectiveLanguage).toBe('zh-CN')
    expect(() => preferences.setGuestLanguage('en-US')).not.toThrow()
    expect(i18n.global.locale.value).toBe('en-US')
  })
})
