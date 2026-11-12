<template>
  <v-app>
    <div v-if="startupError" class="auth-page">
      <v-card class="auth-card">
        <h1 class="auth-title">{{ t('core.errors.startup') }}</h1>
        <v-alert type="error" class="mb-6">{{ startupError }}</v-alert>
        <v-btn block color="primary" @click="reload">{{ t('core.errors.retry') }}</v-btn>
      </v-card>
    </div>
    <AppShell v-else-if="auth.hasAccess && !route.meta.public"><router-view :key="route.path" /></AppShell>
    <router-view v-else-if="auth.ready" />
    <div v-else class="auth-page"><v-progress-circular indeterminate color="primary" /></div>
    <div class="language-menu">
      <v-menu location="top end" :close-on-content-click="false">
        <template #activator="{ props: menuProps }"><v-btn v-bind="menuProps" icon="mdi-translate" variant="tonal" :aria-label="t('core.language.label')" /></template>
        <v-card width="300" class="pa-4"><LanguageSelect :model-value="selectedLanguage" allow-default :loading="savingLanguage" @update:model-value="changeLanguage" /></v-card>
      </v-menu>
    </div>
    <v-snackbar v-model="languageErrorOpen" color="error">{{ languageError }}</v-snackbar>
  </v-app>
</template>

<script setup lang="ts">
import { computed, ref, watchEffect } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import router from './router'
import { useAuthStore } from '@/stores/auth'
import AppShell from '@/components/layout/AppShell.vue'
import LanguageSelect from '@/components/common/LanguageSelect.vue'
import { usePreferencesStore } from '@/stores/preferences'
import type { AppLanguage } from '@/localization/types'

const auth = useAuthStore()
const route = useRoute()
const { t, locale } = useI18n()
const preferences = usePreferencesStore()
const selectedLanguage = computed(() => auth.user ? auth.user.language ?? null : preferences.guestLanguage)
const savingLanguage = ref(false)
const languageError = ref('')
const languageErrorOpen = ref(false)
const startupError = ref('')
watchEffect(() =>
{
  document.documentElement.lang = locale.value
  document.title = t('core.brand.name')
})
router.onError(error => { startupError.value = error.message })
void auth.bootstrap().catch(error => { startupError.value = error instanceof Error ? error.message : t('core.errors.connection') })

async function changeLanguage(language: AppLanguage | null): Promise<void>
{
  if (savingLanguage.value)
  {
    return
  }
  savingLanguage.value = true
  try
  {
    if (auth.user)
    {
      await auth.setLanguage(language)
    }
    else
    {
      preferences.setGuestLanguage(language)
    }
  }
  catch (error)
  {
    languageError.value = error instanceof Error ? error.message : t('core.language.saveFailed')
    languageErrorOpen.value = true
  }
  finally
  {
    savingLanguage.value = false
  }
}
function reload(): void
{
  window.location.reload()
}
</script>

<style scoped>
.language-menu { position: fixed; right: 20px; bottom: 20px; z-index: 900; }
</style>
