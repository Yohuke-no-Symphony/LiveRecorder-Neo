<template>
  <v-card class="video-card" :class="{ 'video-card-linkable': to }">
    <component :is="to ? RouterLink : 'div'" :to="to" :aria-label="to ? title : undefined" class="video-card-content">
      <div class="video-card-cover">
        <v-img v-if="imageSource" :src="imageSource" :aspect-ratio="4 / 3" alt="" cover @error="retry" @load="loaded">
          <template #error>
            <v-icon class="video-card-fallback" icon="mdi-play-box-outline" size="40" aria-hidden="true"/>
          </template>
        </v-img>
        <v-icon v-else class="video-card-fallback" icon="mdi-play-box-outline" size="40" aria-hidden="true"/>
        <span class="video-card-duration mono">{{ formatDuration(durationMs) }}</span>
        <span v-if="to" class="video-card-overlay" aria-hidden="true"><v-icon :icon="linkIcon" size="34"/></span>
      </div>
      <div class="video-card-info">
        <component :is="heading" class="video-card-title" :title="title">{{ title }}</component>
        <div v-if="$slots.metadata" class="video-card-metadata muted"><slot name="metadata"/></div>
        <slot/>
      </div>
    </component>
    <div v-if="$slots.actions" class="video-card-actions"><slot name="actions"/></div>
  </v-card>
</template>

<script setup lang="ts">
import {RouterLink, type RouteLocationRaw} from 'vue-router'
import {useRetryableImage} from '@/composables/useRetryableImage'
import {formatDuration} from '@/utilities/format'

const props = withDefaults(defineProps<{
  title: string
  durationMs: number
  coverUrl?: string
  to?: RouteLocationRaw
  heading?: 'h2' | 'h3'
  linkIcon?: string
}>(), {heading: 'h3', linkIcon: 'mdi-play'})
const {imageSource, retry, loaded} = useRetryableImage(() => props.coverUrl)
</script>

<style scoped>
.video-card {
  display: flex;
  flex-direction: column;
  min-width: 0;
  overflow: hidden;
  transition: transform .18s ease, border-color .18s ease;
}

.video-card-linkable:hover {
  transform: translateY(-3px);
  border-color: rgba(var(--v-theme-primary), .4);
}

.video-card-content {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
}

a.video-card-content:focus-visible {
  outline: 2px solid rgb(var(--v-theme-primary));
  outline-offset: -3px;
}

.video-card-cover {
  position: relative;
  aspect-ratio: 4 / 3;
  background: rgba(var(--v-theme-on-surface), .035);
}

.video-card-fallback {
  position: absolute;
  inset: 0;
  margin: auto;
  color: rgba(var(--v-theme-on-surface), .28);
}

.video-card-duration {
  position: absolute;
  right: 10px;
  bottom: 10px;
  padding: 3px 6px;
  border-radius: 5px;
  background: #000a;
  -webkit-backdrop-filter: var(--ui-backdrop-blur);
  backdrop-filter: var(--ui-backdrop-blur);
  color: white;
  font-size: .7rem;
}

.video-card-overlay {
  display: grid;
  place-items: center;
  position: absolute;
  inset: 0;
  opacity: 0;
  color: white;
  background: #0003;
  transition: opacity .18s;
}

a.video-card-content:hover .video-card-overlay, a.video-card-content:focus-visible .video-card-overlay {
  opacity: 1;
}

.video-card-info {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
  padding: 12px var(--ui-card-padding) 14px;
}

.video-card-title {
  display: -webkit-box;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
  overflow: hidden;
  overflow-wrap: anywhere;
  margin: 0;
  font-size: .94rem;
  font-weight: 550;
  line-height: 1.4;
}

.video-card-metadata {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 4px 12px;
  overflow-wrap: anywhere;
  font-size: .75rem;
  line-height: 1.5;
}

.video-card-actions {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 4px;
  padding: 0 var(--ui-card-padding) 14px;
}
</style>
