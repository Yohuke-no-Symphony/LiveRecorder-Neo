<template>
  <v-select :model-value="modelValue" :items="items" :label="t('core.language.label')" :disabled="disabled"
            :loading="loading" hide-details @update:model-value="select"/>
</template>

<script setup lang="ts">
import {computed} from 'vue'
import {useI18n} from 'vue-i18n'
import {isAppLanguage, LANGUAGE_OPTIONS} from '@/localization/languages'
import type {AppLanguage} from '@/localization/types'

const props = withDefaults(defineProps<{
  modelValue: AppLanguage | null
  allowDefault?: boolean
  disabled?: boolean
  loading?: boolean
}>(), {allowDefault: false, disabled: false, loading: false})
const emit = defineEmits<{ 'update:modelValue': [language: AppLanguage | null] }>()
const {t} = useI18n()
const items = computed(() => [
  ...(props.allowDefault ? [{title: t('core.language.followDefault'), value: null}] : []),
  ...LANGUAGE_OPTIONS,
])

function select(language: unknown): void {
  if (isAppLanguage(language) || (language === null && props.allowDefault)) {
    emit('update:modelValue', language)
  }
}
</script>
