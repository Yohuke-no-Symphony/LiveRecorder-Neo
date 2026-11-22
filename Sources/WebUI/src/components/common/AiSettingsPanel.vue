<template>
  <div class="settings-grid">
    <ContentCard :title="t('aiSettings.service')">
      <div class="form-stack">
        <v-switch v-model="settings.enabled" :label="t('aiSettings.enabled')" :disabled="disabled"/>
        <v-select v-model="settings.provider" :items="providers" :label="t('aiSettings.provider')" :disabled="disabled"
                  @update:model-value="selectProvider"/>
        <v-text-field v-model="settings.baseUrl" :label="t('aiSettings.baseUrl')" :disabled="disabled"
                      :readonly="settings.provider !== 'Custom'" autocomplete="off"/>
        <p class="text-body-2 muted">{{ t('aiSettings.protocol') }}</p>
        <v-text-field v-model="settings.model" :label="t('aiSettings.model')" :disabled="disabled" autocomplete="off"
                      spellcheck="false"/>
        <template v-if="settings.provider === 'DeepSeek'">
          <v-select v-model="settings.thinkingMode" :items="thinkingModes" :label="t('aiSettings.thinkingMode')"
                    :disabled="disabled"/>
          <v-select v-model="settings.reasoningEffort" :items="reasoningEfforts"
                    :label="t('aiSettings.reasoningEffort')"
                    :disabled="disabled || settings.thinkingMode === 'Disabled'"/>
        </template>
        <v-text-field v-model="settings.apiKey" :label="t('aiSettings.apiKey')"
                      :hint="t(settings.hasApiKey ? 'aiSettings.keyStored' : 'aiSettings.keyEmpty')" persistent-hint
                      :disabled="disabled" type="password" autocomplete="new-password"
                      @update:model-value="settings.clearApiKey = false"/>
        <v-checkbox v-if="settings.hasApiKey" v-model="settings.clearApiKey" :label="t('aiSettings.clearKey')"
                    :disabled="disabled"/>
        <v-text-field v-model.number="settings.requestTimeoutSeconds" :label="t('aiSettings.requestTimeoutSeconds')"
                      :error-messages="requestTimeoutValid ? '' : t('aiSettings.invalidRequestLimit')"
                      :disabled="disabled" type="number" min="0" step="1"/>
        <v-text-field v-model.number="settings.maxRequestRetries" :label="t('aiSettings.maxRequestRetries')"
                      :error-messages="requestRetriesValid ? '' : t('aiSettings.invalidRequestLimit')"
                      :disabled="disabled" type="number" min="0" step="1"/>
        <v-btn variant="tonal" prepend-icon="mdi-connection" :loading="testing"
               :disabled="disabled || !settings.model.trim() || !requestSettingsValid" @click="testConnection">{{
          t('aiSettings.test') }}
        </v-btn>
        <p class="text-body-2 muted">{{ t('aiSettings.testHint') }}</p>
        <v-alert v-if="error" type="error" density="compact">{{ error }}</v-alert>
        <v-alert v-if="testResult" type="success" density="compact">{{ t('aiSettings.tested') }}<br/>{{ usageText }}
        </v-alert>
      </div>
    </ContentCard>
    <ContentCard :title="t('aiSettings.tokenBudget')">
      <div class="form-stack">
        <v-text-field v-model.number="settings.tokensPerHour" :label="t('aiSettings.perHour')" :disabled="disabled"
                      type="number" min="1" step="1"/>
        <v-text-field v-model.number="settings.minimumTokens" :label="t('aiSettings.minimum')" :disabled="disabled"
                      type="number" min="1" step="1"/>
        <v-text-field v-model.number="settings.maximumTokens" :label="t('aiSettings.maximum')" :disabled="disabled"
                      type="number" :min="settings.minimumTokens" step="1"/>
        <v-text-field v-model.number="settings.maxOutputTokens" :label="t('aiSettings.maxOutputTokens')"
                      :disabled="disabled" type="number" min="1" step="1"/>
        <v-text-field v-model.number="settings.contextWindowTokens" :label="t('aiSettings.contextWindowTokens')"
                      :error-messages="contextWindowValid ? '' : t('aiSettings.invalidContextWindow')"
                      :disabled="disabled" type="number" min="0" step="1"/>
        <v-text-field v-model.number="settings.maxModelRequests" :label="t('aiSettings.maxModelRequests')"
                      :disabled="disabled" type="number" min="0" step="1"/>
        <v-text-field v-model.number="settings.maxToolCalls" :label="t('aiSettings.maxToolCalls')" :disabled="disabled"
                      type="number" min="0" step="1"/>
      </div>
    </ContentCard>
  </div>
</template>

<script setup lang="ts">
import {computed, ref, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import ContentCard from './ContentCard.vue'
import {AiSettingsAPI} from '@/apis/AiSettingsAPI'
import {useAction} from '@/composables/useAction'
import {isValidAiContextWindow, isValidAiRequestLimit, type AiSettings} from '@/models/settings'

defineProps<{ disabled?: boolean }>()
const settings = defineModel<AiSettings>({required: true})
const {t} = useI18n()
const contextWindowValid = computed(() => isValidAiContextWindow(settings.value))
const requestTimeoutValid = computed(() => isValidAiRequestLimit(settings.value.requestTimeoutSeconds))
const requestRetriesValid = computed(() => isValidAiRequestLimit(settings.value.maxRequestRetries))
const requestSettingsValid = computed(() => contextWindowValid.value && requestTimeoutValid.value && requestRetriesValid.value)
const providers = ['OpenAI', 'DeepSeek', 'Custom']
const thinkingModes = computed(() => [
  {value: 'Default', title: t('aiSettings.followModel')},
  {value: 'Enabled', title: t('aiSettings.thinkingEnabled')},
  {value: 'Disabled', title: t('aiSettings.thinkingDisabled')},
])
const reasoningEfforts = computed(() => [
  {value: 'Default', title: t('aiSettings.followModel')},
  {value: 'Low', title: t('aiSettings.effortLow')},
  {value: 'High', title: t('aiSettings.effortHigh')},
  {value: 'Max', title: t('aiSettings.effortMax')},
])
const {busy: testing, error, run} = useAction()
const testResult = ref<{ promptTokens: number | null; completionTokens: number | null }>()
const configurationSignature = computed(() => JSON.stringify(settings.value))
let configurationRevision = 0
watch(configurationSignature, () => {
  configurationRevision++
  testResult.value = undefined
  error.value = ''
}, {flush: 'sync'})
const usageText = computed(() => testResult.value?.promptTokens != null && testResult.value?.completionTokens != null
  ? t('aiSettings.used', {
    input: testResult.value.promptTokens,
    output: testResult.value.completionTokens
  }) : t('aiSettings.unavailable'))

function selectProvider(provider: string): void {
  if (provider === 'OpenAI') {
    settings.value.baseUrl = 'https://api.openai.com/v1'
  } else if (provider === 'DeepSeek') {
    settings.value.baseUrl = 'https://api.deepseek.com'
  }
  testResult.value = undefined
}

function testConnection(): void {
  if (testing.value || !requestSettingsValid.value) {
    return
  }
  testResult.value = undefined
  const revision = configurationRevision
  const signature = configurationSignature.value
  const snapshot = JSON.parse(signature) as AiSettings
  void run(async () => {
    try {
      const result = await AiSettingsAPI.test(snapshot)
      if (revision === configurationRevision && signature === configurationSignature.value) {
        testResult.value = result
      }
    } catch (failure) {
      if (revision === configurationRevision && signature === configurationSignature.value) {
        throw failure
      }
    }
  })
}
</script>
