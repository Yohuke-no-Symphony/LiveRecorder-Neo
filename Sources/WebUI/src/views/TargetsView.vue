<template>
  <PageHeader :title="t('views.targets.title')"><v-btn color="primary" prepend-icon="mdi-plus" @click="openCreate">{{ t('views.targets.add') }}</v-btn></PageHeader>
  <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
  <div class="summary-grid mb-6"><v-card class="card-content"><div class="stat-label">{{ t('views.targets.recording') }}</div><div class="stat-value mt-2">{{ formatNumber(targets.filter(item => item.state === 'Recording').length) }}</div></v-card><v-card class="card-content"><div class="stat-label">{{ t('views.targets.enabled') }}</div><div class="stat-value mt-2">{{ formatNumber(targets.filter(item => item.enabled).length) }}</div></v-card><v-card class="card-content"><div class="stat-label">{{ t('views.targets.monitored') }}</div><div class="stat-value mt-2">{{ formatNumber(targets.length) }}</div></v-card></div>
  <v-card v-if="targets.length"><v-table><thead><tr><th>{{ t('views.common.streamer') }}</th><th>{{ t('views.common.status') }}</th><th>{{ t('views.targets.account') }}</th><th>{{ t('views.common.quality') }}</th><th>{{ t('views.common.enabled') }}</th><th /></tr></thead><tbody><tr v-for="target in targets" :key="target.id"><td class="py-5"><div class="d-flex align-center ga-3"><v-avatar size="40" color="surface-variant"><v-img :src="MediaAPI.avatar(target.id)" /></v-avatar><div><router-link :to="`/streamers/${target.id}`" class="font-weight-medium">{{ target.name || target.roomId }}</router-link><div class="text-caption muted mt-1">{{ target.roomId }}</div><div v-if="target.error" class="text-caption text-error mt-2">{{ target.error }}</div></div></div></td><td><StatusChip :status="target.state" /></td><td class="text-body-2 muted">{{ accounts.find(account => account.id === target.accountId)?.userName || t('views.targets.default') }}</td><td class="text-body-2 muted">{{ qualityOptions.find(item => item.value === target.quality)?.title || t('views.common.globalSettings') }}</td><td><v-switch :model-value="target.enabled" :disabled="busy" density="compact" :aria-label="t('views.targets.enableTarget', { name: target.name })" @update:model-value="toggle(target, !!$event)" /></td><td><div class="action-buttons"><v-btn icon="mdi-pencil-outline" size="small" variant="text" :aria-label="t('views.targets.edit')" @click="openEdit(target)" /><v-btn icon="mdi-delete-outline" size="small" variant="text" :aria-label="t('views.targets.remove')" @click="deleteTarget = target" /></div></td></tr></tbody></v-table></v-card>
  <EmptyState v-else-if="!busy" :title="t('views.targets.empty')" icon="mdi-record-circle-outline"><v-btn color="primary" prepend-icon="mdi-plus" @click="openCreate">{{ t('views.targets.add') }}</v-btn></EmptyState>
  <TargetAddDialog v-if="createOpen" v-model="createOpen" :accounts="accounts" :monitored-room-ids="targets.map(target => String(target.roomId))" @added="onTargetAdded" />
  <v-dialog v-model="editOpen" max-width="540" :aria-label="t('views.targets.edit')">
    <ContentCard :title="t('views.targets.edit')">
      <form class="form-stack" @submit.prevent="saveEdit">
        <v-select v-model="form.accountId" :label="t('views.targets.bilibiliAccount')" :items="[{ title: t('views.targets.defaultAccount'), value: null }, ...accounts.map(account => ({ title: account.userName, value: account.id }))]" />
        <v-select v-model="form.quality" :label="t('views.common.quality')" :items="[{ title: t('views.common.globalSettings'), value: null }, ...qualityOptions]" />
        <v-select v-model="form.recordDanmu" :label="t('views.targets.recordDanmu')" :items="[{ title: t('views.common.globalSettings'), value: null }, { title: t('views.targets.record'), value: true }, { title: t('views.targets.doNotRecord'), value: false }]" />
        <v-text-field v-model.number="form.segmentGiB" :label="t('views.targets.segmentSize')" type="number" min="0" step="0.5" />
        <v-switch v-model="form.enabled" :label="t('views.targets.enableRecording')" />
        <v-alert v-if="saveError" type="error">{{ saveError }}</v-alert>
        <CardActions embedded>
          <v-btn variant="text" @click="editOpen = false">{{ t('views.common.cancel') }}</v-btn>
          <v-btn color="primary" type="submit" :loading="saveBusy">{{ t('views.common.save') }}</v-btn>
        </CardActions>
      </form>
    </ContentCard>
  </v-dialog>
  <ConfirmDialog :model-value="!!deleteTarget" :title="t('views.targets.removeConfirm')" :message="deleteTarget?.name" :action-label="t('views.targets.remove')" :busy="busy" :error="error" @update:model-value="!$event && (deleteTarget = undefined)" @confirm="removeTarget" />
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import ContentCard from '@/components/common/ContentCard.vue'
import CardActions from '@/components/common/CardActions.vue'
import { computed, reactive, ref } from 'vue'
import { formatNumber } from '@/utilities/format'
import { MediaAPI } from '@/apis/MediaAPI'
import { AuthAPI } from '@/apis/AuthAPI'
import { useAction } from '@/composables/useAction'
import { usePolling } from '@/composables/usePolling'
import type { Streamer } from '@/models/media'
import type { BilibiliAccount } from '@/models/auth'
import { QUALITY_OPTIONS } from '@/models/settings'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import StatusChip from '@/components/common/StatusChip.vue'
import TargetAddDialog from '@/components/targets/TargetAddDialog.vue'

const { t } = useI18n()

const qualityOptions = computed(() => QUALITY_OPTIONS.map(item => ({ value: item.value, title: t(item.titleKey) })))
const streamers = ref<Streamer[]>([])
const targets = computed(() => streamers.value.filter(streamer => streamer.isMonitored !== false))
const accounts = ref<BilibiliAccount[]>([])
const createOpen = ref(false)
const editOpen = ref(false)
const editing = ref<Streamer>()
const deleteTarget = ref<Streamer>()
const form = reactive({ accountId: null as string | null, enabled: true, quality: null as number | null, recordDanmu: null as boolean | null, segmentGiB: null as number | null })
const { busy, error, run } = useAction()
const { busy: saveBusy, error: saveError, run: runSave } = useAction()
async function load(): Promise<void>
{
  await run(async () => { [streamers.value, accounts.value] = await Promise.all([MediaAPI.streamers(), AuthAPI.bilibiliAccounts()]) })
}
function openCreate(): void
{
  createOpen.value = true
}
function onTargetAdded(target: Streamer): void
{
  const index = streamers.value.findIndex(item => item.id === target.id)
  if (index < 0)
  {
    streamers.value.push(target)
  }
  else
  {
    streamers.value[index] = target
  }
}
function openEdit(target: Streamer): void
{
  editing.value = target
  Object.assign(form, { accountId: target.accountId ?? null, enabled: target.enabled, quality: target.quality ?? null, recordDanmu: target.recordDanmu ?? null, segmentGiB: target.segmentBytes ? target.segmentBytes / 1024 ** 3 : null })
  saveError.value = ''
  editOpen.value = true
}
async function saveEdit(): Promise<void>
{
  const target = editing.value
  if (!target)
  {
    return
  }
  await runSave(async () =>
  {
    await MediaAPI.updateStreamer(target.id, { enabled: form.enabled, accountId: form.accountId, quality: form.quality, recordDanmu: form.recordDanmu, segmentBytes: form.segmentGiB && form.segmentGiB > 0 ? Math.round(form.segmentGiB * 1024 ** 3) : null })
    editOpen.value = false
    streamers.value = await MediaAPI.streamers()
  })
}
async function toggle(target: Streamer, enabled: boolean): Promise<void>
{
  await run(async () =>
  {
    await MediaAPI.updateStreamer(target.id, { enabled, accountId: target.accountId, quality: target.quality, recordDanmu: target.recordDanmu, segmentBytes: target.segmentBytes })
    streamers.value = await MediaAPI.streamers()
  })
}
async function removeTarget(): Promise<void>
{
  if (!deleteTarget.value) { return }
  await run(async () => { await MediaAPI.deleteStreamer(deleteTarget.value!.id); deleteTarget.value = undefined; streamers.value = await MediaAPI.streamers() })
}
usePolling(load, 10000)
</script>
