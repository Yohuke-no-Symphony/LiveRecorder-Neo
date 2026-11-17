<template>
  <PageHeader :title="t('shell.nav.workers')" />
  <v-tabs v-model="tab" color="primary" show-arrows class="mb-6" :aria-label="t('shell.nav.workers')">
    <v-tab value="Transcoding">{{ t('transcoding.title') }}</v-tab>
    <v-tab value="Clipping">{{ t('transcoding.clippingTitle') }}</v-tab>
    <v-tab value="Recording">{{ t('transcoding.recordingTitle') }}</v-tab>
    <v-tab value="AI">{{ t('transcoding.aiTitle') }}</v-tab>
  </v-tabs>
  <TranscodingWorkersPanel :key="tab" :kind="tab" />
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import type { WorkerKind } from '@/models/transcoding'
import PageHeader from '@/components/common/PageHeader.vue'
import TranscodingWorkersPanel from '@/components/common/TranscodingWorkersPanel.vue'

const { t } = useI18n()
const tab = ref<WorkerKind>('Transcoding')
</script>
