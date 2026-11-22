<template>
  <v-card :to="pickMode ? undefined : `/streamers/${streamer.id}`" :tag="pickMode ? 'button' : undefined"
          :type="pickMode ? 'button' : undefined" class="streamer-card pa-5" @click="pickMode && emit('pick')">
    <div class="d-flex align-center justify-space-between mb-5">
      <v-avatar size="58" color="surface-variant">
        <v-img :src="MediaAPI.avatar(streamer.id)">
          <template #error>
            <v-icon icon="mdi-account" size="30"/>
          </template>
        </v-img>
      </v-avatar>
      <span v-if="streamer.isLive" class="live-dot" :aria-label="t('components.library.live')"/>
      <v-icon v-else icon="mdi-chevron-right" size="18" class="muted"/>
    </div>
    <h3 class="text-body-1 font-weight-medium text-truncate">
      {{ streamer.name || t('components.library.room', {roomId: streamer.roomId}) }}</h3>
    <div class="text-caption muted mt-2">{{ t('components.library.room', {roomId: streamer.roomId}) }}</div>
  </v-card>
</template>

<script setup lang="ts">
import {useI18n} from 'vue-i18n'
import {MediaAPI} from '@/apis/MediaAPI'
import type {Streamer} from '@/models/media'

const {t} = useI18n()
defineProps<{ streamer: Streamer; pickMode?: boolean }>()
const emit = defineEmits<{ pick: [] }>()
</script>

<style scoped>.streamer-card {
  transition: border-color .18s;
}

.streamer-card:hover {
  border-color: rgba(var(--v-theme-primary), .4);
}

button.streamer-card {
  width: 100%;
  text-align: left;
  font: inherit;
}

.live-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: rgb(var(--v-theme-primary));
  box-shadow: 0 0 0 5px rgba(var(--v-theme-primary), .1);
}</style>
