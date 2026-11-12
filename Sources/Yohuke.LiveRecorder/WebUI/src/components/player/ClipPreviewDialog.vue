<template>
  <v-dialog v-model="opened" max-width="960">
    <v-card>
      <div class="d-flex align-center justify-space-between ga-4 pa-5"><h2 class="text-h6 text-truncate">{{ clip?.title }}</h2><v-btn icon="mdi-close" variant="text" size="small" aria-label="关闭预览" @click="opened = false" /></div>
      <video v-if="opened && clip" :key="clip.id" ref="video" :src="MediaAPI.previewClip(clip.id)" crossorigin="use-credentials" controls playsinline preload="metadata" class="clip-preview" @error="failed = true" />
      <v-alert v-if="failed" type="error" class="ma-5">无法播放此切片</v-alert>
    </v-card>
  </v-dialog>
</template>

<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import type { Clip } from '@/models/media'

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
