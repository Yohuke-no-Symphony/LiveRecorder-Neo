import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { AuthAPI } from '@/apis/AuthAPI'
import { setCsrfToken } from '@/apis/APIBase'
import type { UserProfile } from '@/models/auth'

export const useAuthStore = defineStore('auth', () =>
{
  const user = ref<UserProfile | null>(null)
  const installed = ref(true)
  const requiresToken = ref(true)
  const ready = ref(false)
  const providers = ref<{ id: string; name: string }[]>([])
  const oauthDraft = ref<{ email: string; displayName: string } | null>(null)
  const isAdmin = computed(() => user.value?.role === 'Admin' && user.value.status === 'Active')
  const hasAccess = computed(() => user.value?.status === 'Active')
  let initialLoad: Promise<void> | undefined

  async function refresh(): Promise<void>
  {
    const session = await AuthAPI.me()
    user.value = session.user
    setCsrfToken(session.csrfToken)
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

  async function logout(): Promise<void>
  {
    await AuthAPI.logout()
    user.value = null
    oauthDraft.value = null
    await refresh()
  }

  return { user, installed, requiresToken, ready, providers, oauthDraft, isAdmin, hasAccess, bootstrap, refresh, login, logout }
})
