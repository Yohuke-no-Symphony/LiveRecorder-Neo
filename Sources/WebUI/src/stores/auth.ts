import { computed, ref, watch } from 'vue'
import { defineStore } from 'pinia'
import { AuthAPI } from '@/apis/AuthAPI'
import { setCsrfToken } from '@/apis/APIBase'
import { PERMISSIONS, type PermissionDefinition, type UserProfile } from '@/models/auth'
import type { AppLanguage } from '@/localization/types'
import { usePreferencesStore } from './preferences'

export const useAuthStore = defineStore('auth', () =>
{
  const user = ref<UserProfile | null>(null)
  const preferences = usePreferencesStore()
  watch(user, value => preferences.setSession(value), { immediate: true, flush: 'sync' })
  const installed = ref(true)
  const requiresToken = ref(true)
  const dataDirectory = ref('')
  const recordsDirectory = ref('')
  const ready = ref(false)
  const providers = ref<{ id: string; name: string }[]>([])
  const oauthDraft = ref<{ email: string; displayName: string } | null>(null)
  const isAdmin = computed(() => user.value?.role === 'Admin' && user.value.status === 'Active')
  const hasAccess = computed(() => user.value?.status === 'Active')
  const permissionDefinitions = ref<PermissionDefinition[]>([])
  let permissionLoad: Promise<PermissionDefinition[]> | undefined
  let sessionGeneration = 0
  const analyticsPreferenceSaving = ref(false)
  let analyticsPreferenceRevision = 0
  function hasPermission(permission: string): boolean
  {
    return hasAccess.value && (isAdmin.value || user.value?.permissions?.includes(permission) === true)
  }
  const canViewRecordings = computed(() => hasPermission(PERMISSIONS.viewRecordings))
  const canDownloadRecordings = computed(() => canViewRecordings.value && hasPermission(PERMISSIONS.downloadRecordings))
  const canCreateClips = computed(() => canViewRecordings.value && hasPermission(PERMISSIONS.createClips))
  const canUploadLiveMarkers = computed(() => canViewRecordings.value && hasPermission(PERMISSIONS.uploadLiveMarkers))
  const canGenerateLiveMarkers = computed(() => canViewRecordings.value && hasPermission(PERMISSIONS.generateLiveMarkers))
  const canManageLiveMarkers = computed(() => canViewRecordings.value && hasPermission(PERMISSIONS.manageLiveMarkers))
  const canManageRecordings = computed(() => hasPermission(PERMISSIONS.manageRecordings))
  const canConfigureRecordings = computed(() => canViewRecordings.value && hasPermission(PERMISSIONS.configureRecordings))
  const canViewAnalytics = computed(() => canViewRecordings.value && hasPermission(PERMISSIONS.viewAnalytics))
  const canGenerateAnalytics = computed(() => canViewAnalytics.value && hasPermission(PERMISSIONS.generateAnalytics))
  const canPurgeRecordings = computed(() => hasPermission(PERMISSIONS.purgeRecordings))
  const homePath = computed(() => canViewRecordings.value ? '/' : '/account')
  let initialLoad: Promise<void> | undefined

  async function refresh(): Promise<void>
  {
    const operation = sessionGeneration
    const preferenceRevision = analyticsPreferenceRevision
    const session = await AuthAPI.me()
    if (operation !== sessionGeneration) { return }
    user.value = preserveAnalyticsPreference(session.user, preferenceRevision)
    setCsrfToken(session.csrfToken)
    if (hasAccess.value)
    {
      permissionLoad ??= AuthAPI.permissions().catch(error =>
      {
        permissionLoad = undefined
        throw error
      })
      const definitions = await permissionLoad
      if (operation === sessionGeneration) { permissionDefinitions.value = definitions }
    }
  }

  async function bootstrap(): Promise<void>
  {
    initialLoad ??= (async () =>
    {
      try
      {
        const status = await AuthAPI.setupStatus()
        installed.value = status.installed
        requiresToken.value = status.requiresToken
        dataDirectory.value = status.installed ? '' : status.dataDirectory ?? ''
        recordsDirectory.value = status.installed ? '' : status.recordsDirectory ?? ''
        await preferences.refreshDefaults()
        await refresh()
        if (installed.value)
        {
          providers.value = await AuthAPI.providers()
        }
        ready.value = true
      }
      catch (error)
      {
        initialLoad = undefined
        throw error
      }
    })()
    await initialLoad
  }

  async function login(email: string, password: string): Promise<void>
  {
    const operation = ++sessionGeneration
    analyticsPreferenceSaving.value = false
    const session = await AuthAPI.login(email, password)
    if (operation !== sessionGeneration) { return }
    user.value = session.user
    setCsrfToken(session.csrfToken)
  }

  async function setLanguage(language: AppLanguage | null): Promise<void>
  {
    const operation = sessionGeneration
    const preferenceRevision = analyticsPreferenceRevision
    const updated = await AuthAPI.setLanguage(language)
    if (operation === sessionGeneration) { user.value = preserveAnalyticsPreference(updated, preferenceRevision) }
  }

  function preserveAnalyticsPreference(profile: UserProfile | null, revision: number): UserProfile | null
  {
    return profile && profile.id === user.value?.id && (analyticsPreferenceSaving.value || revision !== analyticsPreferenceRevision)
      ? { ...profile, analyticsRangePinned: user.value.analyticsRangePinned ?? true }
      : profile
  }

  async function setAnalyticsRangePinned(rangePinned: boolean): Promise<void>
  {
    if (!user.value || analyticsPreferenceSaving.value) { return }
    const operation = sessionGeneration
    const userId = user.value.id
    const previous = user.value.analyticsRangePinned ?? true
    analyticsPreferenceRevision++
    analyticsPreferenceSaving.value = true
    user.value = { ...user.value, analyticsRangePinned: rangePinned }
    try
    {
      const updated = await AuthAPI.setAnalyticsRangePinned(rangePinned)
      if (operation === sessionGeneration && user.value?.id === userId)
      {
        user.value = { ...user.value, analyticsRangePinned: updated.analyticsRangePinned ?? true }
      }
    }
    catch (error)
    {
      if (operation === sessionGeneration && user.value?.id === userId)
      {
        user.value = { ...user.value, analyticsRangePinned: previous }
      }
      throw error
    }
    finally
    {
      if (operation === sessionGeneration)
      {
        analyticsPreferenceRevision++
        analyticsPreferenceSaving.value = false
      }
    }
  }

  async function logout(): Promise<void>
  {
    await AuthAPI.logout()
    clearSession()
    await refresh()
  }

  function clearSession(): void
  {
    sessionGeneration++
    analyticsPreferenceSaving.value = false
    user.value = null
    oauthDraft.value = null
    permissionDefinitions.value = []
    permissionLoad = undefined
    setCsrfToken('')
  }

  return { user, installed, requiresToken, dataDirectory, recordsDirectory, ready, providers, oauthDraft, isAdmin, hasAccess, canManageRecordings, canConfigureRecordings, canViewAnalytics, canGenerateAnalytics, canPurgeRecordings, canViewRecordings, canDownloadRecordings, canCreateClips, canUploadLiveMarkers, canGenerateLiveMarkers, canManageLiveMarkers, permissionDefinitions, hasPermission, homePath, bootstrap, refresh, login, logout, clearSession, setLanguage, analyticsPreferenceSaving, setAnalyticsRangePinned }
})
