<template>
  <v-dialog v-model="opened" :persistent="saving" max-width="680" :aria-label="t('shell.recordingConfiguration.title')">
    <ContentCard :title="t('shell.recordingConfiguration.title')">
      <template #actions>
        <v-btn variant="text" :disabled="saving" @click="opened = false">{{ t('components.common.cancel') }}</v-btn>
        <v-btn color="primary" :loading="saving" :disabled="loading || saving || !configuration || (!inherit && !pattern.trim())" @click="save">{{ t('shell.permissions.save') }}</v-btn>
      </template>
      <v-progress-linear v-if="loading" indeterminate color="primary" class="mb-4" />
      <v-alert v-if="error" type="error" class="mb-4">{{ error }}</v-alert>
      <p class="text-body-2 muted mb-4">{{ t('shell.recordingConfiguration.description') }}</p>
      <div v-if="configuration" class="form-stack">
        <v-switch v-model="inherit" :label="t('shell.recordingConfiguration.inherit')" :disabled="saving || loading" />
        <v-text-field v-model="pattern" :label="t('shell.recordingConfiguration.pattern')" :disabled="inherit || saving || loading" spellcheck="false" autocomplete="off" />
        <div class="text-caption muted">{{ t('shell.recordingConfiguration.effective') }}<code class="effective-pattern">{{ configuration.effectiveTranslationDanmuPattern }}</code></div>
      </div>
      <v-btn class="mt-4" variant="text" prepend-icon="mdi-refresh" :disabled="saving || loading" @click="load">{{ t('shell.recordingConfiguration.reload') }}</v-btn>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import { onScopeDispose, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import ContentCard from '@/components/common/ContentCard.vue'
import { SessionConfigurationAPI } from '@/apis/SessionConfigurationAPI'
import type { SessionConfiguration } from '@/models/sessionConfiguration'

const opened = defineModel<boolean>({ required: true })
const props = defineProps<{ sessionId: string }>()
const emit = defineEmits<{ saved: [configuration: SessionConfiguration] }>()
const { t } = useI18n()
const configuration = ref<SessionConfiguration>()
const inherit = ref(true)
const pattern = ref('')
const loading = ref(false)
const saving = ref(false)
const error = ref('')
let generation = 0

async function load(): Promise<void>
{
  const operation = ++generation
  loading.value = true
  saving.value = false
  error.value = ''
  configuration.value = undefined
  try
  {
    const value = await SessionConfigurationAPI.get(props.sessionId)
    if (operation !== generation)
    {
      return
    }
    configuration.value = value
    inherit.value = value.translationDanmuPatternOverride === null
    pattern.value = value.translationDanmuPatternOverride ?? ''
  }
  catch (value)
  {
    if (operation === generation)
    {
      error.value = value instanceof Error ? value.message : t('shell.recordingConfiguration.failed')
    }
  }
  finally
  {
    if (operation === generation)
    {
      loading.value = false
    }
  }
}

async function save(): Promise<void>
{
  if (!configuration.value || saving.value)
  {
    return
  }
  const operation = ++generation
  saving.value = true
  error.value = ''
  try
  {
    const value = await SessionConfigurationAPI.update(props.sessionId, inherit.value ? null : pattern.value, configuration.value.revision)
    if (operation === generation)
    {
      emit('saved', value)
      opened.value = false
    }
  }
  catch (value)
  {
    if (operation === generation)
    {
      error.value = value instanceof Error ? value.message : t('shell.recordingConfiguration.failed')
    }
  }
  finally
  {
    if (operation === generation)
    {
      saving.value = false
    }
  }
}

watch([opened, () => props.sessionId], ([value]) =>
{
  if (value)
  {
    void load()
  }
  else
  {
    generation++
    loading.value = false
    saving.value = false
  }
}, { immediate: true })
onScopeDispose(() => { generation++ })
</script>

<style scoped>
.effective-pattern { display: block; margin-top: 8px; white-space: pre-wrap; overflow-wrap: anywhere; }
</style>
