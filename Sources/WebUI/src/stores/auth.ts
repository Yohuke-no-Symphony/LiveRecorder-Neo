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
  function hasPermission(permission: string): boolean
  {
    return hasAccess.value && (isAdmin.value || user.value?.permissions?.includes(permission) === true)
  }
  const canViewRecordings = computed(() => hasPermission(PERMISSIONS.viewRecordings))
  const canDownloadRecordings = computed(() => canViewRecordings.value && hasPermission(PERMISSIONS.downloadRecordings))
  const canCreateClips = computed(() => canViewRecordings.value && hasPermission(PERMISSIONS.createClips))
  const canUploadLiveMarkers = computed(() => canViewRecordings.value && hasPermission(PERMISSIONS.uploadLiveMarkers))
  const canManageLiveMarkers = computed(() => canViewRecordings.value && hasPermission(PERMISSIONS.manageLiveMarkers))
  const canManageRecordings = computed(() => hasPermission(PERMISSIONS.manageRecordings))
  const canViewAnalytics = computed(() => canViewRecordings.value && hasPermission(PERMISSIONS.viewAnalytics))
  const canPurgeRecordings = computed(() => hasPermission(PERMISSIONS.purgeRecordings))
  const homePath = computed(() => canViewRecordings.value ? '/' : '/account')
  let initialLoad: Promise<void> | undefined

  async function refresh(): Promise<void>
  {
    const session = await AuthAPI.me()
    user.value = session.user
    setCsrfToken(session.csrfToken)
    if (hasAccess.value)
    {
      permissionLoad ??= AuthAPI.permissions().catch(error =>
      {
        permissionLoad = undefined
        throw error
      })
      permissionDefinitions.value = await permissionLoad
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
    const session = await AuthAPI.login(email, password)
    user.value = session.user
    setCsrfToken(session.csrfToken)
  }

  async function setLanguage(language: AppLanguage | null): Promise<void>
  {
    user.value = await AuthAPI.setLanguage(language)
  }

  async function logout(): Promise<void>
  {
    await AuthAPI.logout()
    user.value = null
    oauthDraft.value = null
    await refresh()
  }

  return { user, installed, requiresToken, dataDirectory, recordsDirectory, ready, providers, oauthDraft, isAdmin, hasAccess, canManageRecordings, canViewAnalytics, canPurgeRecordings, canViewRecordings, canDownloadRecordings, canCreateClips, canUploadLiveMarkers, canManageLiveMarkers, permissionDefinitions, hasPermission, homePath, bootstrap, refresh, login, logout, setLanguage }
})
