<template>
  <template v-if="auth.canManageClips">
    <PageHeader :title="t('shell.nav.clipManagement')">
      <v-btn color="error" variant="outlined" prepend-icon="mdi-delete-outline"
             :disabled="loading || saving || !selectedClips.length" @click="openTrash(selectedClips)">
        {{ t('views.clips.trashSelected', { count: formatNumber(selectedClips.length) }) }}
      </v-btn>
      <v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="loading" :disabled="saving" @click="load">{{
        t('views.common.refresh') }}
      </v-btn>
    </PageHeader>
    <v-alert v-if="loadError" class="mb-6" type="error">{{ loadError }}</v-alert>
    <v-card>
      <v-data-table-server
        v-model="selected"
        v-model:page="page"
        v-model:items-per-page="pageSize"
        v-model:sort-by="sortBy"
        :headers="headers"
        :items="items"
        :items-length="total"
        :items-per-page-options="[25, 50, 100]"
        :loading="loading"
        :no-data-text="t('views.clips.empty')"
        :aria-label="t('shell.nav.clipManagement')"
        :hide-no-data="!!loadError"
        item-value="id"
        select-strategy="page"
        must-sort
        show-select
      >
        <template #item.title="{ item }"><span class="clip-table-title" :title="item.title">{{ item.title }}</span>
        </template>
        <template #item.ownerDisplayName="{ item }">
          <UserIdentityLink :user-id="item.ownerProfileId"
                            :name="item.ownerDisplayName" :avatar-url="item.ownerAvatarUrl" :size="26"/>
        </template>
        <template #item.duration="{ item }">{{ formatDuration(item.endMs - item.startMs) }}</template>
        <template #item.bytes="{ item }">{{ formatBytes(item.bytes) }}</template>
        <template #item.createdAtUtc="{ item }">
          <time :datetime="item.createdAtUtc">{{ formatDate(item.createdAtUtc) }}</time>
        </template>
        <template #item.expiresAtUtc="{ item }">
          <time :datetime="item.expiresAtUtc">{{ formatDate(item.expiresAtUtc) }}</time>
        </template>
        <template #item.actions="{ item }">
          <div class="action-buttons">
            <v-btn icon="mdi-play" size="small" variant="text" :title="t('views.common.play')"
                   :aria-label="t('views.common.play')" :disabled="!auth.canViewRecordings"
                   @click="previewClip = item"/>
            <v-btn icon="mdi-download" size="small" variant="text" :title="t('views.common.download')"
                   :aria-label="t('views.common.download')"
                   :href="auth.canDownloadRecordings ? MediaAPI.downloadClip(item.id) : undefined"
                   :disabled="!auth.canDownloadRecordings"/>
            <v-btn icon="mdi-share-variant-outline" size="small" variant="text" :title="t('sharing.clipTitle')"
                   :aria-label="t('sharing.clipTitle')" @click="sharingClip = item"/>
            <v-btn icon="mdi-delete-outline" size="small" color="error" variant="text"
                   :title="t('views.common.moveToTrash')" :aria-label="t('views.common.moveToTrash')"
                   :disabled="loading || saving" @click="openTrash([item])"/>
          </div>
        </template>
      </v-data-table-server>
    </v-card>
    <ConfirmDialog
      class="clip-management-confirm"
      :model-value="!!pendingTrash"
      :title="t('views.clips.trashConfirm')"
      :message="trashMessage"
      :action-label="t('views.common.moveToTrash')"
      :busy="saving"
      :error="trashError"
      @update:model-value="!$event && !saving && (pendingTrash = undefined)"
      @confirm="trash"
    />
    <ClipPreviewDialog :model-value="!!previewClip" :clip="previewClip"
                       @update:model-value="!$event && (previewClip = undefined)"/>
    <ClipShareDialog v-if="sharingClip" :clip="sharingClip" :model-value="true"
                     @update:model-value="!$event && (sharingClip = undefined)"/>
  </template>
</template>

<script setup lang="ts">
import {computed, onBeforeUnmount, ref, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import {MediaAPI} from '@/apis/MediaAPI'
import ClipShareDialog from '@/components/clips/ClipShareDialog.vue'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import PageHeader from '@/components/common/PageHeader.vue'
import UserIdentityLink from '@/components/common/UserIdentityLink.vue'
import ClipPreviewDialog from '@/components/player/ClipPreviewDialog.vue'
import {useAction} from '@/composables/useAction'
import type {ManagedClip} from '@/models/clipManagement'
import {useAuthStore} from '@/stores/auth'
import {formatBytes, formatDate, formatDuration, formatNumber} from '@/utilities/format'

const {t} = useI18n()
const auth = useAuthStore()
const page = ref(1)
const pageSize = ref(25)
const sortBy = ref<readonly { key: string; order?: 'asc' | 'desc' | boolean }[]>([{key: 'createdAtUtc', order: 'desc'}])
const items = ref<ManagedClip[]>([])
const total = ref(0)
const selected = ref<string[]>([])
const loading = ref(false)
const loadError = ref('')
const pendingTrash = ref<ManagedClip[]>()
const previewClip = ref<ManagedClip>()
const sharingClip = ref<ManagedClip>()
const {busy: saving, error: trashError, run} = useAction()
let activeLoad: AbortController | undefined

const headers = computed(() => [
  {key: 'title', title: t('views.trash.name'), cellProps: {class: 'py-5'}},
  {key: 'ownerDisplayName', title: t('views.clips.owner')},
  {key: 'duration', title: t('views.session.duration'), cellProps: {class: 'text-caption mono'}},
  {key: 'bytes', title: t('views.common.size'), cellProps: {class: 'text-caption mono'}},
  {key: 'createdAtUtc', title: t('views.common.createdAt'), cellProps: {class: 'text-caption muted'}},
  {key: 'expiresAtUtc', title: t('views.common.expiresAt'), cellProps: {class: 'text-caption muted'}},
  {key: 'actions', title: '', sortable: false},
])
const selectedClips = computed(() => {
  const ids = new Set(selected.value)
  return items.value.filter(clip => ids.has(clip.id))
})
const trashMessage = computed(() => pendingTrash.value?.length === 1 ? pendingTrash.value[0]?.title
  : t('views.clips.trashCount', {count: formatNumber(pendingTrash.value?.length ?? 0)}))

async function load(): Promise<void> {
  activeLoad?.abort()
  if (!auth.canManageClips) {
    return
  }
  const controller = new AbortController()
  activeLoad = controller
  loading.value = true
  loadError.value = ''
  const sort = sortBy.value[0]
  try {
    const result = await MediaAPI.managedClips({
      page: page.value, pageSize: pageSize.value, sort: sort?.key,
      direction: sort?.order === 'asc' ? 'asc' : 'desc',
    }, controller.signal)
    if (controller.signal.aborted) {
      return
    }
    const lastPage = Math.max(1, Math.ceil(result.total / pageSize.value))
    if (page.value > lastPage) {
      page.value = lastPage
      return
    }
    items.value = result.items
    total.value = result.total
    const availableIds = new Set(result.items.map(clip => clip.id))
    selected.value = selected.value.filter(id => availableIds.has(id))
  } catch (error) {
    if (!controller.signal.aborted) {
      loadError.value = error instanceof Error ? error.message : t('components.common.operationFailed')
    }
  } finally {
    if (activeLoad === controller) {
      activeLoad = undefined
      loading.value = false
    }
  }
}

function openTrash(clips: ManagedClip[]): void {
  if (saving.value || loading.value || !clips.length) {
    return
  }
  trashError.value = ''
  pendingTrash.value = clips
}

async function trash(): Promise<void> {
  const clips = pendingTrash.value
  if (!clips?.length || saving.value || !auth.canManageClips) {
    return
  }
  await run(async () => {
    const results = await MediaAPI.trashManagedClips(clips.map(clip => clip.id))
    const byId = new Map(results.map(result => [result.clipId, result]))
    const moved = new Set(results.filter(result => result.success).map(result => result.clipId))
    const failed = clips.filter(clip => !moved.has(clip.id))
    items.value = items.value.filter(clip => !moved.has(clip.id))
    selected.value = selected.value.filter(id => !moved.has(id))
    pendingTrash.value = failed.length ? failed : undefined
    trashError.value = failed.map(clip => t('views.clips.trashFailure', {
      title: clip.title, message: byId.get(clip.id)?.error || t('components.common.operationFailed'),
    })).join('\n')
    await load()
  })
}

watch([page, pageSize, sortBy, () => auth.canManageClips], () => {
  selected.value = []
  items.value = []
  if (!auth.canManageClips) {
    pendingTrash.value = undefined
    previewClip.value = undefined
    sharingClip.value = undefined
  }
  void load()
}, {immediate: true})
onBeforeUnmount(() => activeLoad?.abort())
</script>

<style scoped>
.clip-table-title {
  display: block;
  max-width: 28rem;
  overflow-wrap: anywhere;
}

.clip-management-confirm :deep(.v-alert__content) {
  white-space: pre-line;
}
</style>
