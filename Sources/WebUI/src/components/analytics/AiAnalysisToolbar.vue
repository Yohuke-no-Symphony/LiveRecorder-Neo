<template>
  <div class="ai-analysis-toolbar">
    <v-menu location="bottom end">
      <template #activator="{ props: menuProps }">
        <v-btn v-bind="menuProps" color="primary" variant="tonal" append-icon="mdi-chevron-down">{{ t('replay.aiMenu') }}</v-btn>
      </template>
      <v-list density="compact" min-width="180">
        <v-list-item v-if="canGenerate" :disabled="!canStart" :title="t(hasResult ? 'replay.regenerate' : 'replay.generate')" prepend-icon="mdi-creation" role="menuitem" @click="emit('generate')" />
        <v-list-item :title="t('replay.details')" prepend-icon="mdi-information-outline" role="menuitem" @click="emit('details')" />
        <v-list-item v-if="canGenerateLiveMarkers" :disabled="!canStartLiveMarkers" :title="t('liveMarkers.generate')" prepend-icon="mdi-playlist-edit" role="menuitem" @click="emit('generateLiveMarkers')" />
        <v-list-item v-if="canViewLiveMarkers" :title="t('liveMarkers.generationDetails')" prepend-icon="mdi-information-outline" role="menuitem" @click="emit('liveMarkerDetails')" />
      </v-list>
    </v-menu>
  </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'

defineProps<{ canGenerate: boolean; canStart: boolean; hasResult: boolean; canGenerateLiveMarkers?: boolean; canStartLiveMarkers?: boolean; canViewLiveMarkers?: boolean }>()
const emit = defineEmits<{ generate: []; details: []; generateLiveMarkers: []; liveMarkerDetails: [] }>()
const { t } = useI18n()
</script>

<style scoped>
.ai-analysis-toolbar { display: flex; align-items: center; }
</style>
