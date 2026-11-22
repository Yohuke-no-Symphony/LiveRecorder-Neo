<template>
  <v-text-field :model-value="link" :label="t('sharing.link')" readonly spellcheck="false" density="compact"/>
</template>

<script setup lang="ts">
import {useI18n} from 'vue-i18n'

defineProps<{ link: string }>()
const {t} = useI18n()
</script>
