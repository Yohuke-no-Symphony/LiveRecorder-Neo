<template>
  <PageHeader :title="auth.isAdmin ? '切片' : '我的切片'"><v-btn variant="outlined" to="/jobs" prepend-icon="mdi-progress-clock">后台任务</v-btn></PageHeader>
  <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
  <div v-if="clips.length" class="media-grid">
    <v-card v-for="clip in clips" :key="clip.id" class="clip-card">
      <router-link :to="`/sessions/${clip.sessionId}?at=${clip.startMs}`"><v-img :src="MediaAPI.cover(clip.sessionId)" :aspect-ratio="16 / 9" cover><div class="clip-length mono">{{ formatDuration(clip.endMs - clip.startMs) }}</div></v-img></router-link>
      <div class="pa-5"><h2 class="text-body-1 font-weight-medium text-truncate mb-2">{{ clip.title }}</h2><div class="text-caption muted">{{ formatDuration(clip.startMs, true) }} — {{ formatDuration(clip.endMs, true) }} · {{ formatBytes(clip.bytes) }}</div><div class="text-caption muted mt-3 mb-5">到期 {{ formatDate(clip.expiresAtUtc) }}</div><div class="d-flex ga-2"><v-btn color="primary" size="small" prepend-icon="mdi-play" :disabled="clip.isTrashed || new Date(clip.expiresAtUtc).getTime() <= Date.now()" @click="preview(clip)">播放</v-btn><v-btn :href="MediaAPI.downloadClip(clip.id)" variant="tonal" size="small" prepend-icon="mdi-download" :disabled="clip.isTrashed || new Date(clip.expiresAtUtc).getTime() <= Date.now()">下载</v-btn><v-spacer /><v-btn icon="mdi-delete-outline" size="small" variant="text" aria-label="移入回收站" @click="selected = clip" /></div></div>
    </v-card>
  </div>
  <EmptyState v-else-if="!busy" title="暂无切片" icon="mdi-content-cut"><v-btn color="primary" to="/">浏览录播</v-btn></EmptyState>
  <ConfirmDialog :model-value="!!selected" title="将切片移入回收站？" :message="selected?.title" action-label="移入回收站" :busy="busy" :error="error" @update:model-value="!$event && (selected = undefined)" @confirm="deleteClip" />
  <ClipPreviewDialog v-model="previewOpen" :clip="previewedClip" />
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAuthStore } from '@/stores/auth'
import { useAction } from '@/composables/useAction'
import { usePolling } from '@/composables/usePolling'
import type { Clip } from '@/models/media'
import { formatBytes, formatDate, formatDuration } from '@/utilities/format'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import ClipPreviewDialog from '@/components/player/ClipPreviewDialog.vue'

const auth = useAuthStore()
const clips = ref<Clip[]>([])
const selected = ref<Clip>()
const previewedClip = ref<Clip>()
const previewOpen = ref(false)
const { busy, error, run } = useAction()
async function load(): Promise<void>
{
  await run(async () => { clips.value = (await MediaAPI.clips()).sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc)) })
}
async function deleteClip(): Promise<void>
{
  if (!selected.value) { return }
  await run(async () =>
  {
    await MediaAPI.deleteClip(selected.value!.id)
    selected.value = undefined
    clips.value = await MediaAPI.clips()
  })
}
function preview(clip: Clip): void
{
  if (clip.isTrashed || new Date(clip.expiresAtUtc).getTime() <= Date.now()) { return }
  previewedClip.value = clip
  previewOpen.value = true
}
usePolling(load, 10000)
</script>

<style scoped>.clip-card { overflow: hidden; }.clip-length { position: absolute; right: 12px; bottom: 12px; background: #000b; color: white; font-size: .75rem; padding: 3px 6px; border-radius: 5px; }</style>
