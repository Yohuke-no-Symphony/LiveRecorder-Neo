<template>
  <v-menu location="bottom end">
    <template #activator="{ props: menuProps }">
      <v-btn v-bind="menuProps" icon="mdi-creation" variant="text" size="small"
             :aria-label="t('liveMarkers.aiActions')"/>
    </template>
    <v-list density="compact" min-width="180">
      <v-list-item v-if="canGenerate" :disabled="!canStart" :title="t('liveMarkers.generate')"
                   prepend-icon="mdi-playlist-edit" @click="emit('generate')"/>
      <v-list-item :title="t('liveMarkers.generationDetails')" prepend-icon="mdi-information-outline"
                   @click="emit('details')"/>
    </v-list>
  </v-menu>
</template>

<script setup lang="ts">
import {useI18n} from 'vue-i18n'

defineProps<{ canGenerate: boolean; canStart: boolean }>()
const emit = defineEmits<{ generate: []; details: [] }>()
const {t} = useI18n()
</script>
