<template>
  <PageHeader :title="auth.isAdmin ? t('views.common.clips') : t('views.clips.mine')"><v-btn variant="outlined" to="/jobs" prepend-icon="mdi-progress-clock">{{ t('views.common.jobs') }}</v-btn></PageHeader>
  <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
  <div v-if="clips.length" class="media-grid">
    <ClipCard v-for="clip in clips" :key="clip.id" :clip="clip" @preview="preview(clip)" @trash="selected = clip" />
  </div>
  <EmptyState v-else-if="!busy" :title="t('views.clips.empty')" icon="mdi-content-cut"><v-btn color="primary" to="/">{{ t('views.clips.browse') }}</v-btn></EmptyState>
  <ConfirmDialog :model-value="!!selected" :title="t('views.clips.trashConfirm')" :message="selected?.title" :action-label="t('views.common.moveToTrash')" :busy="busy" :error="error" @update:model-value="!$event && (selected = undefined)" @confirm="deleteClip" />
  <ClipPreviewDialog v-model="previewOpen" :clip="previewedClip" />
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { ref } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAuthStore } from '@/stores/auth'
import { useAction } from '@/composables/useAction'
import { usePolling } from '@/composables/usePolling'
import type { Clip } from '@/models/media'
import ClipCard from '@/components/clips/ClipCard.vue'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import ClipPreviewDialog from '@/components/player/ClipPreviewDialog.vue'

const { t } = useI18n()

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
