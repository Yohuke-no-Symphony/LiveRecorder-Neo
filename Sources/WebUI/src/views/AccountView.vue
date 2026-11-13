<template>
  <PageHeader :title="t('shell.account.title')" />
  <v-alert v-if="error || saveError" type="error" class="mb-6">{{ error || saveError }}</v-alert>
  <ContentCard class="mb-6" style="max-width: 720px" :title="t('shell.account.language')">
    <div class="form-stack">
      <LanguageSelect :model-value="auth.user?.language ?? null" allow-default :disabled="saving || savingTranslation" :loading="saving" @update:model-value="saveLanguage" />
      <p class="text-body-2 muted">{{ t('shell.account.languageDescription') }}</p>
    </div>
  </ContentCard>
  <ContentCard class="mb-6" style="max-width: 720px" :title="t('shell.account.translationSettings')">
    <form class="form-stack" @submit.prevent="saveTranslation">
      <v-switch v-model="inheritTranslationPattern" :label="t('shell.account.inheritTranslationPattern')" :disabled="saving || savingTranslation" />
      <v-text-field v-model="translationPattern" :label="t('shell.account.translationDanmuPattern')" :placeholder="t('shell.account.translationPatternPlaceholder')" :disabled="inheritTranslationPattern || saving || savingTranslation" spellcheck="false" autocomplete="off" />
      <v-alert v-if="translationError" type="error">{{ translationError }}</v-alert>
      <v-btn type="submit" color="primary" :loading="savingTranslation" :disabled="saving || savingTranslation">{{ t('shell.account.saveTranslation') }}</v-btn>
    </form>
  </ContentCard>
  <h2 class="section-heading">{{ t('shell.account.loginMethods') }}</h2>
  <v-progress-linear v-if="busy && !providers.length" indeterminate color="primary" />
  <v-card v-if="providers.length" style="max-width: 720px"><v-list bg-color="transparent" class="pa-2"><v-list-item v-for="provider in providers" :key="provider.id" :title="provider.name" prepend-icon="mdi-account-key-outline" class="py-4"><template #append><v-btn :variant="boundIds.includes(provider.id) ? 'text' : 'tonal'" :color="boundIds.includes(provider.id) ? 'success' : 'primary'" :disabled="boundIds.includes(provider.id) || !!bindingId" :loading="bindingId === provider.id" :prepend-icon="boundIds.includes(provider.id) ? 'mdi-check' : 'mdi-link-variant'" @click="bind(provider.id)">{{ boundIds.includes(provider.id) ? t('shell.account.bound') : t('shell.account.bind') }}</v-btn></template></v-list-item></v-list></v-card>
  <EmptyState v-else-if="!busy" :title="t('shell.account.noProviders')" icon="mdi-account-key-outline" />
  <v-snackbar v-model="saved" color="success">{{ t('shell.account.languageSaved') }}</v-snackbar>
  <v-snackbar v-model="translationSaved" color="success">{{ t('shell.account.translationSaved') }}</v-snackbar>
</template>

<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import ContentCard from '@/components/common/ContentCard.vue'
import { useI18n } from 'vue-i18n'
import { AuthAPI } from '@/apis/AuthAPI'
import { useAction } from '@/composables/useAction'
import { useAuthStore } from '@/stores/auth'
import type { AppLanguage } from '@/localization/types'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import LanguageSelect from '@/components/common/LanguageSelect.vue'

const { t } = useI18n()
const auth = useAuthStore()
const providers = ref<{ id: string; name: string }[]>([])
const boundIds = ref<string[]>([])
const bindingId = ref('')
const saved = ref(false)
const translationSaved = ref(false)
const inheritTranslationPattern = ref(true)
const translationPattern = ref('')
const { busy, error, run } = useAction()
const { busy: saving, error: saveError, run: runSave } = useAction()
const { busy: savingTranslation, error: translationError, run: runSaveTranslation } = useAction()
watch([() => auth.user?.id, () => auth.user?.translationDanmuPattern], ([, pattern]) =>
{
  inheritTranslationPattern.value = pattern == null
  translationPattern.value = pattern ?? ''
  translationError.value = ''
  translationSaved.value = false
}, { immediate: true })
onMounted(() => { void run(async () => { [providers.value, boundIds.value] = await Promise.all([AuthAPI.providers(), AuthAPI.identities()]) }) })
async function saveLanguage(language: AppLanguage | null): Promise<void>
{
  await runSave(async () =>
  {
    await auth.setLanguage(language)
    saved.value = true
  })
}
async function saveTranslation(): Promise<void>
{
  translationSaved.value = false
  const userId = auth.user?.id
  await runSaveTranslation(async () =>
  {
    await auth.setTranslationDanmuPattern(inheritTranslationPattern.value ? null : translationPattern.value)
    translationSaved.value = auth.user?.id === userId
  })
}
async function bind(id: string): Promise<void>
{
  bindingId.value = id
  await run(async () =>
  {
    const result = await AuthAPI.oauthBind(id)
    sessionStorage.setItem('oauth_return_to', '/account')
    window.location.assign(result.authorizationUrl)
  })
  bindingId.value = ''
}
</script>
