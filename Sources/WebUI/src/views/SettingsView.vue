<template>
  <PageHeader :title="t('shell.settings.title')"><v-btn color="primary" prepend-icon="mdi-check" :loading="saving" :disabled="!settings" @click="save">{{ t('shell.settings.save') }}</v-btn></PageHeader>
  <v-alert v-if="error || saveError" type="error" class="mb-6">{{ error || saveError }}</v-alert>
  <v-progress-linear v-if="loading" indeterminate color="primary" />
  <template v-if="settings">
    <v-tabs v-model="tab" color="primary" show-arrows class="mb-6"><v-tab value="general">{{ t('shell.settings.general') }}</v-tab><v-tab value="recording">{{ t('shell.settings.recording') }}</v-tab><v-tab value="media">{{ t('shell.settings.media') }}</v-tab><v-tab value="registration">{{ t('shell.settings.registration') }}</v-tab><v-tab value="smtp">{{ t('shell.settings.mail') }}</v-tab><v-tab value="oauth">{{ t('shell.settings.oauth') }}</v-tab></v-tabs>
    <div v-if="tab === 'general'" class="settings-grid">
      <v-card class="form-section">
        <h2>{{ t('shell.settings.interface') }}</h2>
        <div class="form-stack">
          <LanguageSelect :model-value="settings.defaultLanguage" :disabled="saving" @update:model-value="selectDefaultLanguage" />
          <p class="text-body-2 muted">{{ t('shell.settings.defaultLanguageDescription') }}</p>
        </div>
      </v-card>
      <v-card class="form-section">
        <h2>{{ t('components.dataDirectories.title') }}</h2>
        <DataDirectorySettings v-if="paths" :paths="paths" @saved="paths = $event" />
      </v-card>
    </div>
    <div v-else-if="tab === 'recording'" class="settings-grid">
      <v-card class="form-section"><h2>{{ t('shell.settings.recordingSettings') }}</h2><div class="form-stack"><v-select v-model="settings.quality" :label="t('shell.settings.quality')" :items="qualityOptions" /><v-switch v-model="settings.recordDanmu" :label="t('shell.settings.recordDanmu')" /><v-text-field v-model.number="segmentGiB" :label="t('shell.settings.segmentSize')" type="number" min="0.1" step="0.5" /><v-text-field v-model.number="settings.monitorIntervalSeconds" :label="t('shell.settings.monitorInterval')" type="number" min="5" /><v-text-field v-model.number="settings.sessionGraceMinutes" :label="t('shell.settings.sessionGrace')" type="number" min="1" /></div></v-card>
      <v-card class="form-section"><h2>{{ t('shell.settings.filesAndTools') }}</h2><div class="form-stack"><v-text-field v-model="settings.timeZone" :label="t('shell.settings.timeZone')" /><v-text-field v-model="settings.ffmpegPath" :label="t('shell.settings.ffmpeg')" /><v-text-field v-model="settings.ffprobePath" :label="t('shell.settings.ffprobe')" /></div></v-card>
    </div>
    <div v-else-if="tab === 'media'" class="settings-grid">
      <v-card class="form-section"><h2>{{ t('shell.settings.retention') }}</h2><div class="form-stack"><v-text-field v-model.number="settings.clipRetentionDays" :label="t('shell.settings.clipRetention')" type="number" min="1" /><v-text-field v-model.number="settings.trashRetentionDays" :label="t('shell.settings.trashRetention')" type="number" min="1" /><v-text-field v-model.number="settings.cacheRetentionMinutes" :label="t('shell.settings.cacheRetention')" type="number" min="1" /></div></v-card>
      <v-card class="form-section"><h2>{{ t('shell.settings.resourceLimits') }}</h2><div class="form-stack"><v-text-field v-model.number="cacheGiB" :label="t('shell.settings.cacheLimit')" type="number" min="1" /><v-text-field v-model.number="minimumFreeGiB" :label="t('shell.settings.minimumFree')" type="number" min="1" /><v-text-field v-model.number="settings.mediaConcurrency" :label="t('shell.settings.mediaConcurrency')" type="number" min="1" max="32" /><v-text-field v-model.number="settings.playbackConcurrency" :label="t('shell.settings.playbackConcurrency')" type="number" min="1" max="32" /></div></v-card>
    </div>
    <v-card v-else-if="tab === 'registration'" class="form-section" style="max-width: 660px"><h2>{{ t('shell.settings.registrationSettings') }}</h2><v-switch v-model="settings.registrationEnabled" :label="t('shell.settings.registrationEnabled')" /><v-divider class="my-6" /><v-btn variant="tonal" prepend-icon="mdi-account-group-outline" to="/admin/users">{{ t('shell.settings.manageUsers') }}</v-btn></v-card>
    <v-card v-else-if="tab === 'smtp'" class="form-section" style="max-width: 800px">
      <h2>{{ t('shell.settings.smtp') }}</h2>
      <div class="form-stack">
        <div class="d-flex ga-4">
          <v-text-field v-model="settings.smtp.host" :label="t('shell.settings.host')" />
          <v-text-field v-model.number="settings.smtp.port" :label="t('shell.settings.port')" type="number" min="1" max="65535" style="max-width: 130px" />
        </div>
        <v-switch v-model="settings.smtp.useSsl" :label="t('shell.settings.ssl')" />
        <v-switch v-model="settings.smtp.checkCertificateRevocation" :label="t('shell.settings.checkCertificateRevocation')" :hint="t('shell.settings.certificateRevocationDescription')" persistent-hint />
        <v-text-field v-model="settings.smtp.userName" :label="t('shell.settings.userName')" autocomplete="off" />
        <v-text-field v-model="settings.smtp.password" :label="t('shell.settings.password')" type="password" autocomplete="new-password" />
        <v-text-field v-model="settings.smtp.fromAddress" :label="t('shell.settings.fromAddress')" type="email" />
        <v-text-field v-model="settings.smtp.fromName" :label="t('shell.settings.fromName')" />
      </div>
    </v-card>
    <template v-else-if="tab === 'oauth'">
      <div class="d-flex justify-end mb-5"><v-btn variant="tonal" prepend-icon="mdi-plus" @click="addProvider">{{ t('shell.settings.addProvider') }}</v-btn></div>
      <div class="form-stack"><v-card v-for="(provider, index) in settings.oauthProviders" :key="index" class="form-section"><div class="d-flex align-center justify-space-between mb-6"><h2 class="mb-0">{{ provider.name || t('shell.settings.provider') }}</h2><div class="d-flex align-center ga-3"><v-switch v-model="provider.enabled" :label="t('shell.settings.enabled')" density="compact" /><v-btn icon="mdi-delete-outline" variant="text" size="small" :aria-label="t('shell.settings.deleteProvider')" @click="settings.oauthProviders.splice(index, 1)" /></div></div><div class="settings-grid"><div class="form-stack"><v-text-field v-model="provider.id" :label="t('shell.settings.id')" /><v-text-field v-model="provider.name" :label="t('shell.settings.name')" /><v-text-field v-model="provider.authorizationEndpoint" :label="t('shell.settings.authorizationEndpoint')" /><v-text-field v-model="provider.tokenEndpoint" :label="t('shell.settings.tokenEndpoint')" /><v-text-field v-model="provider.userInfoEndpoint" :label="t('shell.settings.userInfoEndpoint')" /><v-text-field :model-value="`${origin}/oauth/callback/${provider.id}`" :label="t('shell.settings.callback')" readonly /></div><div class="form-stack"><v-text-field v-model="provider.clientId" :label="t('shell.settings.clientId')" autocomplete="off" /><v-text-field v-model="provider.clientSecret" :label="t('shell.settings.clientSecret')" type="password" autocomplete="new-password" /><v-text-field v-model="provider.scope" :label="t('shell.settings.scope')" /><v-text-field v-model="provider.subjectField" :label="t('shell.settings.subjectField')" /><v-text-field v-model="provider.nameField" :label="t('shell.settings.nameField')" /><div class="d-flex ga-4"><v-text-field v-model="provider.emailField" :label="t('shell.settings.emailField')" /><v-text-field v-model="provider.avatarField" :label="t('shell.settings.avatarField')" /></div><v-switch v-model="provider.useBasicAuthentication" :label="t('shell.settings.basicAuthentication')" /></div></div></v-card></div>
      <EmptyState v-if="!settings.oauthProviders.length" :title="t('shell.settings.noProviders')" icon="mdi-account-key-outline" />
    </template>
  </template>
  <v-snackbar v-model="saved" color="success">{{ t('shell.settings.saved') }}</v-snackbar>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed, onMounted, ref } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAction } from '@/composables/useAction'
import { useAuthStore } from '@/stores/auth'
import { usePreferencesStore } from '@/stores/preferences'
import type { AppLanguage } from '@/localization/types'
import type { ServerPaths, ServerSettings } from '@/models/settings'
import { QUALITY_OPTIONS } from '@/models/settings'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import LanguageSelect from '@/components/common/LanguageSelect.vue'
import DataDirectorySettings from '@/components/common/DataDirectorySettings.vue'

const { t } = useI18n()
const preferences = usePreferencesStore()
const qualityOptions = computed(() => QUALITY_OPTIONS.map(item => ({ value: item.value, title: t(item.titleKey) })))
const tab = ref('general')
const settings = ref<ServerSettings>()
const paths = ref<ServerPaths>()
const saved = ref(false)
const origin = window.location.origin
const { busy: loading, error, run } = useAction()
const { busy: saving, error: saveError, run: runSave } = useAction()
const auth = useAuthStore()
const byteField = (key: 'segmentBytes' | 'cacheLimitBytes' | 'minimumFreeBytes') => computed({
  get: () => settings.value ? settings.value[key] / 1024 ** 3 : 0,
  set: (value: number) => { if (settings.value) { settings.value[key] = Math.round(Number(value) * 1024 ** 3) } },
})
const segmentGiB = byteField('segmentBytes')
const cacheGiB = byteField('cacheLimitBytes')
const minimumFreeGiB = byteField('minimumFreeBytes')
function selectDefaultLanguage(language: AppLanguage | null): void
{
  if (settings.value && language)
  {
    settings.value.defaultLanguage = language
  }
}
function clearSecrets(value: ServerSettings): ServerSettings
{
  value.smtp.password = ''
  value.oauthProviders ??= []
  for (const provider of value.oauthProviders) { provider.clientSecret = '' }
  return value
}
onMounted(() =>
{
  void run(async () =>
  {
    const [loadedSettings, loadedPaths] = await Promise.all([MediaAPI.settings(), MediaAPI.settingsPaths()])
    settings.value = clearSecrets(loadedSettings)
    paths.value = loadedPaths
  })
})
function addProvider(): void
{
  settings.value?.oauthProviders.push({ id: '', name: '', enabled: false, authorizationEndpoint: '', tokenEndpoint: '', userInfoEndpoint: '', clientId: '', clientSecret: '', scope: 'profile email', subjectField: 'sub', nameField: 'displayName', emailField: 'email', avatarField: 'avatarUrl', useBasicAuthentication: false })
}
async function save(): Promise<void>
{
  if (!settings.value) { return }
  await runSave(async () =>
  {
    if (!Number.isFinite(settings.value!.segmentBytes) || settings.value!.segmentBytes <= 0) { throw new Error(t('shell.settings.invalidSegmentSize')) }
    if (settings.value!.oauthProviders.some(provider => !provider.id.trim() || !provider.name.trim())) { throw new Error(t('shell.settings.invalidProvider')) }
    await MediaAPI.updateSettings(settings.value!)
    settings.value = clearSecrets(await MediaAPI.settings())
    preferences.setDefaultLanguage(settings.value.defaultLanguage)
    auth.providers = await (await import('@/apis/AuthAPI')).AuthAPI.providers()
    saved.value = true
  })
}
</script>
