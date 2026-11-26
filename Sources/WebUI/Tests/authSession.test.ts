// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, disposePinia, setActivePinia, type Pinia } from 'pinia'
import { AuthAPI } from '../src/apis/AuthAPI'
import { useAuthStore } from '../src/stores/auth'
import type { UserProfile, UserSession } from '../src/models/auth'

let pinia: Pinia
const profile: UserProfile = { id: 'viewer', email: 'viewer@example.test', displayName: '观众', status: 'Active', role: 'User', permissions: ['recordings.view'] }

beforeEach(() =>
{
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() })
  pinia = createPinia()
  setActivePinia(pinia)
})
afterEach(() =>
{
  disposePinia(pinia)
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('会话清理与旧响应隔离', () =>
{
  it('改密清理登录后，旧身份刷新响应不能恢复旧用户', async () =>
  {
    let complete!: (session: UserSession) => void
    vi.spyOn(AuthAPI, 'me').mockImplementation(() => new Promise(resolve => { complete = resolve }))
    const auth = useAuthStore()
    auth.user = profile
    const pending = auth.refresh()
    auth.clearSession()
    complete({ user: profile, csrfToken: 'obsolete' })
    await pending
    expect(auth.user).toBeNull()
    expect(auth.canViewRecordings).toBe(false)
  })

  it('退出后的旧语言保存响应也不能恢复身份', async () =>
  {
    let complete!: (user: UserProfile) => void
    vi.spyOn(AuthAPI, 'setLanguage').mockImplementation(() => new Promise(resolve => { complete = resolve }))
    const auth = useAuthStore()
    auth.user = profile
    const pending = auth.setLanguage('en-US')
    auth.clearSession()
    complete({ ...profile, language: 'en-US' })
    await pending
    expect(auth.user).toBeNull()
  })

  it('保存显示名称后，已发出的会话刷新保留新名称', async () =>
  {
    let complete!: (session: UserSession) => void
    vi.spyOn(AuthAPI, 'me').mockImplementation(() => new Promise(resolve => { complete = resolve }))
    vi.spyOn(AuthAPI, 'permissions').mockResolvedValue([])
    vi.spyOn(AuthAPI, 'setDisplayName').mockResolvedValue({ ...profile, displayName: '新名称' })
    const auth = useAuthStore()
    auth.user = profile
    const pending = auth.refresh()

    await auth.setDisplayName('新名称')
    complete({ user: profile, csrfToken: 'current' })
    await pending

    expect(auth.user?.displayName).toBe('新名称')
  })
})
