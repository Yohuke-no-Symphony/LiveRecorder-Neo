<template>
  <PageHeader title="录制管理"><v-btn color="primary" prepend-icon="mdi-plus" @click="openCreate">添加直播间</v-btn></PageHeader>
  <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
  <div class="d-flex ga-5 mb-7"><v-card class="pa-5 flex-grow-1"><div class="stat-label">录制中</div><div class="stat-value mt-2">{{ targets.filter(item => item.state === 'Recording').length }}</div></v-card><v-card class="pa-5 flex-grow-1"><div class="stat-label">已启用</div><div class="stat-value mt-2">{{ targets.filter(item => item.enabled).length }}</div></v-card><v-card class="pa-5 flex-grow-1"><div class="stat-label">监控目标</div><div class="stat-value mt-2">{{ targets.length }}</div></v-card></div>
  <v-card v-if="targets.length"><v-table><thead><tr><th>主播</th><th>状态</th><th>录制账号</th><th>清晰度</th><th>启用</th><th /></tr></thead><tbody><tr v-for="target in targets" :key="target.id"><td class="py-5"><div class="d-flex align-center ga-3"><v-avatar size="40" color="surface-variant"><v-img :src="MediaAPI.avatar(target.id)" /></v-avatar><div><router-link :to="`/streamers/${target.id}`" class="font-weight-medium">{{ target.name || target.roomId }}</router-link><div class="text-caption muted mt-1">{{ target.roomId }}</div><div v-if="target.error" class="text-caption text-error mt-2">{{ target.error }}</div></div></div></td><td><StatusChip :status="target.state" /></td><td class="text-body-2 muted">{{ accounts.find(account => account.id === target.accountId)?.userName || '默认' }}</td><td class="text-body-2 muted">{{ QUALITY_OPTIONS.find(item => item.value === target.quality)?.title || '全局设置' }}</td><td><v-switch :model-value="target.enabled" :disabled="busy" density="compact" :aria-label="`启用${target.name}`" @update:model-value="toggle(target, !!$event)" /></td><td><div class="action-buttons"><v-btn icon="mdi-pencil-outline" size="small" variant="text" aria-label="编辑录制" @click="openEdit(target)" /><v-btn icon="mdi-delete-outline" size="small" variant="text" aria-label="移除监控" @click="deleteTarget = target" /></div></td></tr></tbody></v-table></v-card>
  <EmptyState v-else-if="!busy" title="暂无监控目标" icon="mdi-record-circle-outline"><v-btn color="primary" prepend-icon="mdi-plus" @click="openCreate">添加直播间</v-btn></EmptyState>
  <v-dialog v-model="dialogOpen" max-width="540"><v-card class="pa-6"><h2 class="text-h6 mb-6">{{ editing ? '编辑录制' : '添加直播间' }}</h2><form class="form-stack" @submit.prevent="save"><v-text-field v-if="!editing" v-model.number="form.roomId" label="直播间号" type="number" min="1" required /><v-select v-model="form.accountId" label="B站账号" :items="[{ title: '默认账号', value: null }, ...accounts.map(account => ({ title: account.userName, value: account.id }))]" /><template v-if="editing"><v-select v-model="form.quality" label="清晰度" :items="[{ title: '全局设置', value: null }, ...QUALITY_OPTIONS]" /><v-select v-model="form.recordDanmu" label="录制弹幕" :items="[{ title: '全局设置', value: null }, { title: '录制', value: true }, { title: '不录制', value: false }]" /><v-text-field v-model.number="form.segmentGiB" label="分段大小（GiB）" type="number" min="0" step="0.5" /></template><v-switch v-model="form.enabled" label="启用录制" /><v-alert v-if="saveError" type="error">{{ saveError }}</v-alert><div class="d-flex justify-end ga-3"><v-btn variant="text" @click="dialogOpen = false">取消</v-btn><v-btn color="primary" type="submit" :loading="saveBusy">保存</v-btn></div></form></v-card></v-dialog>
  <ConfirmDialog :model-value="!!deleteTarget" title="移除监控？" :message="deleteTarget?.name" action-label="移除监控" :busy="busy" :error="error" @update:model-value="!$event && (deleteTarget = undefined)" @confirm="removeTarget" />
</template>

<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
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

const streamers = ref<Streamer[]>([])
const targets = computed(() => streamers.value.filter(streamer => streamer.isMonitored !== false))
const accounts = ref<BilibiliAccount[]>([])
const dialogOpen = ref(false)
const editing = ref<Streamer>()
const deleteTarget = ref<Streamer>()
const form = reactive({ roomId: undefined as number | undefined, accountId: null as string | null, enabled: true, quality: null as number | null, recordDanmu: null as boolean | null, segmentGiB: null as number | null })
const { busy, error, run } = useAction()
const { busy: saveBusy, error: saveError, run: runSave } = useAction()
async function load(): Promise<void>
{
  await run(async () => { [streamers.value, accounts.value] = await Promise.all([MediaAPI.streamers(), AuthAPI.bilibiliAccounts()]) })
}
function openCreate(): void
{
  editing.value = undefined
  Object.assign(form, { roomId: undefined, accountId: null, enabled: true, quality: null, recordDanmu: null, segmentGiB: null })
  saveError.value = ''
  dialogOpen.value = true
}
function openEdit(target: Streamer): void
{
  editing.value = target
  Object.assign(form, { roomId: target.roomId, accountId: target.accountId ?? null, enabled: target.enabled, quality: target.quality ?? null, recordDanmu: target.recordDanmu ?? null, segmentGiB: target.segmentBytes ? target.segmentBytes / 1024 ** 3 : null })
  saveError.value = ''
  dialogOpen.value = true
}
async function save(): Promise<void>
{
  await runSave(async () =>
  {
    if (editing.value)
    {
      await MediaAPI.updateStreamer(editing.value.id, { enabled: form.enabled, accountId: form.accountId, quality: form.quality, recordDanmu: form.recordDanmu, segmentBytes: form.segmentGiB && form.segmentGiB > 0 ? Math.round(form.segmentGiB * 1024 ** 3) : null })
    }
    else
    {
      if (!form.roomId || form.roomId < 1) { throw new Error('请输入有效直播间号') }
      await MediaAPI.createStreamer({ roomId: form.roomId, accountId: form.accountId, enabled: form.enabled })
    }
    dialogOpen.value = false
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
