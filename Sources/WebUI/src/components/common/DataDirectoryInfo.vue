<template>
  <div class="form-stack">
    <v-text-field :model-value="dataDirectory" :label="t('components.dataDirectories.data')" :readonly="!editable" :required="editable" @update:model-value="emit('update:dataDirectory', $event ?? '')" />
    <v-text-field v-if="editable || recordsDirectory !== undefined" :model-value="recordsDirectory" :label="t('components.dataDirectories.records')" :readonly="!editable" :required="editable" @update:model-value="emit('update:recordsDirectory', $event ?? '')" />
    <p class="text-body-2 muted">{{ t('components.dataDirectories.description') }}</p>
  </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'

defineProps<{ dataDirectory: string; recordsDirectory?: string; editable?: boolean }>()
const emit = defineEmits<{ 'update:dataDirectory': [value: string]; 'update:recordsDirectory': [value: string] }>()
const { t } = useI18n()
</script>
