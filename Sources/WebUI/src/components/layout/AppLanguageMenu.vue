<template>
  <v-menu location="bottom end" min-width="200" max-width="calc(100vw - 24px)">
    <template #activator="{ props: menuProps }">
      <v-btn v-bind="menuProps" icon="mdi-translate" variant="text" :loading="savingLanguage"
             :aria-label="t('core.language.label')"/>
    </template>
    <v-list density="compact" role="menu" :aria-label="t('core.language.label')">
      <v-list-item
        v-for="option in options"
        :key="option.value ?? 'default'"
        :title="option.title"
        :active="option.value === selectedLanguage"
        :aria-checked="option.value === selectedLanguage"
        :disabled="savingLanguage"
        color="primary"
        role="menuitemradio"
        @click="changeLanguage(option.value)"
      />
    </v-list>
  </v-menu>
  <v-snackbar v-model="languageErrorOpen" color="error">{{ languageError }}</v-snackbar>
</template>

<script setup lang="ts">
import {computed, ref} from 'vue'
import {useI18n} from 'vue-i18n'
import {LANGUAGE_OPTIONS} from '@/localization/languages'
import type {AppLanguage} from '@/localization/types'
import {useAuthStore} from '@/stores/auth'
import {usePreferencesStore} from '@/stores/preferences'

const {t} = useI18n()
const auth = useAuthStore()
const preferences = usePreferencesStore()
const options = computed(() => [{title: t('core.language.followDefault'), value: null}, ...LANGUAGE_OPTIONS])
const selectedLanguage = computed(() => auth.user ? auth.user.language ?? null : preferences.guestLanguage)
const savingLanguage = ref(false)
const languageError = ref('')
const languageErrorOpen = ref(false)

async function changeLanguage(language: AppLanguage | null): Promise<void> {
  if (savingLanguage.value || language === selectedLanguage.value) {
    return
  }
  savingLanguage.value = true
  try {
    if (auth.user) {
      await auth.setLanguage(language)
    } else {
      preferences.setGuestLanguage(language)
    }
  } catch (error) {
    languageError.value = error instanceof Error ? error.message : t('core.language.saveFailed')
    languageErrorOpen.value = true
  } finally {
    savingLanguage.value = false
  }
}
</script>
