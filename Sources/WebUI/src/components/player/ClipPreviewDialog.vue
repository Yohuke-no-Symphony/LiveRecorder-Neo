<template>
  <v-dialog v-model="opened" max-width="960" :aria-label="clip?.title">
    <v-card>
      <CardHeader :title="clip?.title ?? ''">
        <template #actions><v-btn icon="mdi-close" variant="text" size="small" :aria-label="t('components.player.closePreview')" @click="opened = false" /></template>
      </CardHeader>
      <video v-if="opened && clip" :key="clip.id" ref="video" :src="MediaAPI.previewClip(clip.id)" crossorigin="use-credentials" controls playsinline preload="metadata" class="clip-preview" @error="failed = true" />
      <v-alert v-if="failed" type="error" class="ma-5">{{ t('components.player.previewFailed') }}</v-alert>
    </v-card>
  </v-dialog>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { onBeforeUnmount, ref, watch } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import type { Clip } from '@/models/media'
import CardHeader from '@/components/common/CardHeader.vue'

const { t } = useI18n()

const opened = defineModel<boolean>({ required: true })
defineProps<{ clip?: Clip }>()
const video = ref<HTMLVideoElement>()
const failed = ref(false)
watch(opened, value =>
{
  failed.value = false
  if (!value) { video.value?.pause() }
})
onBeforeUnmount(() => { video.value?.pause() })
</script>

<style scoped>.clip-preview { display: block; width: 100%; max-height: 75dvh; aspect-ratio: 16 / 9; background: #000; }</style>
