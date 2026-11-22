<template>
  <form class="form-stack" @submit.prevent="save">
    <v-text-field v-model="configuredDataDirectory" :label="t('components.dataDirectories.data')" :disabled="busy"
                  required/>
    <v-text-field v-model="configuredRecordsDirectory" :label="t('components.dataDirectories.records')" :disabled="busy"
                  required/>
    <p class="text-body-2 muted">{{ t('components.dataDirectories.relativePath') }}</p>
    <v-text-field :model-value="paths.dataDirectory" :label="t('components.dataDirectories.active')" readonly/>
    <v-text-field :model-value="paths.recordsDirectory" :label="t('components.dataDirectories.activeRecords')"
                  readonly/>
    <v-text-field :model-value="paths.configFile" :label="t('components.dataDirectories.configFile')" readonly/>
    <p class="text-body-2 muted">{{ t('components.dataDirectories.migration') }}</p>
    <v-alert v-if="paths.restartRequired" type="info">{{ t('components.dataDirectories.restartRequired') }}</v-alert>
    <v-alert v-if="error" type="error">{{ error }}</v-alert>
    <v-btn type="submit" color="primary" :loading="busy" :disabled="!canSave">{{ t('components.dataDirectories.save')
      }}
    </v-btn>
  </form>
  <v-snackbar v-model="saved" color="success">{{ t('components.dataDirectories.saved') }}</v-snackbar>
</template>

<script setup lang="ts">
import {computed, ref, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import {MediaAPI} from '@/apis/MediaAPI'
import {useAction} from '@/composables/useAction'
import type {ServerPaths} from '@/models/settings'

const props = defineProps<{ paths: ServerPaths }>()
const emit = defineEmits<{ saved: [paths: ServerPaths] }>()
const {t} = useI18n()
const configuredDataDirectory = ref(props.paths.configuredDataDirectory)
const configuredRecordsDirectory = ref(props.paths.configuredRecordsDirectory)
const saved = ref(false)
const {busy, error, run} = useAction()

const canSave = computed(() => !busy.value
  && configuredDataDirectory.value.trim().length > 0
  && configuredRecordsDirectory.value.trim().length > 0
  && (configuredDataDirectory.value !== props.paths.configuredDataDirectory || configuredRecordsDirectory.value !== props.paths.configuredRecordsDirectory))

watch(() => props.paths.configuredDataDirectory, value => {
  configuredDataDirectory.value = value
})
watch(() => props.paths.configuredRecordsDirectory, value => {
  configuredRecordsDirectory.value = value
})
watch([configuredDataDirectory, configuredRecordsDirectory], () => {
  saved.value = false
}, {flush: 'sync'})

async function save(): Promise<void> {
  if (!canSave.value) {
    return
  }
  saved.value = false
  await run(async () => {
    const updated = await MediaAPI.updateSettingsPaths({
      dataDirectory: configuredDataDirectory.value,
      recordsDirectory: configuredRecordsDirectory.value
    })
    emit('saved', updated)
    configuredDataDirectory.value = updated.configuredDataDirectory
    configuredRecordsDirectory.value = updated.configuredRecordsDirectory
    saved.value = true
  })
}
</script>
