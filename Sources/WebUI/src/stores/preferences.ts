import { computed, ref, watch } from 'vue'
import { defineStore } from 'pinia'
import { request } from '@/apis/APIBase'
import { i18n } from '@/plugins/i18n'
import { DEFAULT_LANGUAGE, isAppLanguage, readGuestLanguage, writeGuestLanguage } from '@/localization/languages'
import type { AppLanguage } from '@/localization/types'
import type { UserProfile } from '@/models/auth'

export const usePreferencesStore = defineStore('preferences', () =>
{
  const defaultLanguage = ref<AppLanguage>(DEFAULT_LANGUAGE)
  const userLanguage = ref<AppLanguage | null>(null)
  const guestLanguage = ref<AppLanguage | null>(readGuestLanguage())
  const signedIn = ref(false)
  const effectiveLanguage = computed(() => (signedIn.value ? userLanguage.value : guestLanguage.value) ?? defaultLanguage.value)

  watch(effectiveLanguage, language => { i18n.global.locale.value = language }, { immediate: true, flush: 'sync' })

  function setDefaultLanguage(language: unknown): void
  {
    defaultLanguage.value = isAppLanguage(language) ? language : DEFAULT_LANGUAGE
  }

  function setSession(user: UserProfile | null): void
  {
    userLanguage.value = isAppLanguage(user?.language) ? user.language : null
    signedIn.value = user !== null
  }

  function setGuestLanguage(language: AppLanguage | null): void
  {
    guestLanguage.value = isAppLanguage(language) ? language : null
    writeGuestLanguage(guestLanguage.value)
  }

  async function refreshDefaults(): Promise<void>
  {
    const settings = await request<{ defaultLanguage: AppLanguage }>('ui-settings')
    setDefaultLanguage(settings.defaultLanguage)
  }

  return { defaultLanguage, userLanguage, guestLanguage, signedIn, effectiveLanguage, setDefaultLanguage, setSession, setGuestLanguage, refreshDefaults }
})
