<template>
  <v-dialog :model-value="open" max-width="760" scrollable :persistent="saveBusy" :aria-labelledby="titleId" @update:model-value="updateOpen">
    <ContentCard :title="t('views.targets.add')" :title-id="titleId">
      <div class="target-add-options mb-5">
        <v-select v-model="accountId" :label="t('views.targets.bilibiliAccount')" :items="accountOptions" :disabled="saveBusy" hide-details />
        <v-switch v-model="enabled" :label="t('views.targets.enableRecording')" :disabled="saveBusy" hide-details />
      </div>
      <v-tabs v-model="tab" color="primary" class="mb-5">
        <v-tab value="search" :disabled="saveBusy">{{ t('components.targets.automaticSearch') }}</v-tab>
        <v-tab value="manual" :disabled="saveBusy">{{ t('components.targets.manualAdd') }}</v-tab>
      </v-tabs>
      <v-window v-model="tab">
        <v-window-item value="search" class="pt-3">
          <form class="room-search-form" @submit.prevent="search">
            <v-text-field name="roomSearch" :model-value="keyword" :label="t('components.targets.searchLabel')"
                          :disabled="saveBusy" maxlength="200" clearable hide-details @update:model-value="keyword = $event ?? ''" />
            <v-btn type="submit" color="primary" variant="tonal" :loading="loading" :disabled="saveBusy">{{ t('components.targets.search') }}</v-btn>
          </form>
          <p class="text-caption muted mt-3">{{ t('components.targets.searchHint') }}</p>
          <v-alert v-if="searchError" type="error" variant="tonal" class="mt-4">{{ searchError }}</v-alert>
          <v-alert v-for="(issue, index) in issues" :key="index" type="warning" variant="tonal" class="mt-4">{{ describeIssue(issue) }}</v-alert>
          <RoomSearchResults :results="results" :monitored-room-ids="monitoredRoomIds" :busy="saveBusy" :saving-room-id="savingRoomId" @add="addRoom($event.roomId)" />
          <p v-if="searched && !results.length && !issues.length && !searchError" class="text-body-2 muted text-center py-8">{{ t('components.targets.noResults') }}</p>
          <div v-if="hasMore" class="d-flex justify-center mt-4">
            <v-btn variant="text" :loading="loadingMore" :disabled="saveBusy || loading" @click="loadMore">{{ t('components.common.loadMore') }}</v-btn>
          </div>
        </v-window-item>
        <v-window-item value="manual" class="pt-3">
          <form :id="manualFormId" class="manual-room-form" @submit.prevent="addRoom(manualRoomId)">
            <v-text-field v-model="manualRoomId" name="roomId" :label="t('views.targets.roomId')" inputmode="numeric"
                          autocomplete="off" :disabled="saveBusy" :hint="t('components.targets.manualHint')" persistent-hint />
          </form>
        </v-window-item>
      </v-window>
      <v-alert v-if="saveError" type="error" variant="tonal" class="mt-4">{{ saveError }}</v-alert>
      <template #actions>
        <v-btn variant="text" :disabled="saveBusy" @click="updateOpen(false)">{{ t('views.common.cancel') }}</v-btn>
        <v-btn v-if="tab === 'manual'" type="submit" :form="manualFormId" color="primary" variant="flat" :loading="saveBusy">{{ t('components.targets.add') }}</v-btn>
      </template>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import { computed, ref, useId, watch } from 'vue'
import ContentCard from '@/components/common/ContentCard.vue'
import { useI18n } from 'vue-i18n'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAction } from '@/composables/useAction'
import { useLiveRoomSearch } from '@/composables/useLiveRoomSearch'
import type { BilibiliAccount } from '@/models/auth'
import type { BilibiliRoomSearchIssue } from '@/models/bilibili'
import type { Streamer } from '@/models/media'
import RoomSearchResults from './RoomSearchResults.vue'

const props = defineProps<{ accounts: BilibiliAccount[]; monitoredRoomIds: readonly string[] }>()
const open = defineModel<boolean>({ required: true })
const emit = defineEmits<{ added: [target: Streamer] }>()
const { t } = useI18n()
const titleId = useId()
const manualFormId = useId()
const tab = ref('search')
const accountId = ref<string | null>(null)
const enabled = ref(true)
const manualRoomId = ref('')
const savingRoomId = ref<string | null>(null)
const accountOptions = computed(() => [{ title: t('views.targets.defaultAccount'), value: null }, ...props.accounts.map(account => ({ title: account.userName, value: account.id }))])
const { busy: saveBusy, error: saveError, run: runSave } = useAction()
const { keyword, results, issues, loading, loadingMore, hasMore, searched, error: searchError, search, loadMore } = useLiveRoomSearch(() => accountId.value)

function updateOpen(value: boolean): void
{
  if (!saveBusy.value)
  {
    open.value = value
  }
}

function describeIssue(issue: BilibiliRoomSearchIssue): string
{
  const message = issue.code ? t(`components.targets.issues.${issue.code}`) : issue.message || t('components.targets.issues.unavailable')
  const detail = issue.apiCode === null ? message : t('components.targets.issueCode', { message, code: issue.apiCode })
  const source = issue.sources.map(item => t(`components.targets.sources.${item}`)).join(' / ')
  return source ? t('components.targets.sourceIssue', { source, message: detail }) : detail
}

async function addRoom(value: string): Promise<void>
{
  if (saveBusy.value)
  {
    return
  }
  const normalized = value.trim()
  const roomId = Number(normalized)
  if (!/^\d+$/.test(normalized) || !Number.isSafeInteger(roomId) || roomId <= 0)
  {
    saveError.value = t('views.targets.invalidRoomId')
    return
  }
  savingRoomId.value = normalized
  await runSave(async () =>
  {
    const target = await MediaAPI.createStreamer({ roomId, accountId: accountId.value, enabled: enabled.value })
    emit('added', target)
    open.value = false
  })
  savingRoomId.value = null
}

watch([tab, accountId], () => { saveError.value = '' })
</script>

<style scoped>
.target-add-options { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; gap: 16px; }
.room-search-form { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; gap: 12px; }
@media (max-width: 480px) {
  .target-add-options { grid-template-columns: minmax(0, 1fr); gap: 8px; }
}
</style>
