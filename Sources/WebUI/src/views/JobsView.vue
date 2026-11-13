<template>
  <PageHeader :title="t('views.common.jobs')"><v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="busy" @click="load">{{ t('views.common.refresh') }}</v-btn></PageHeader>
  <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
  <v-card v-if="jobs.length">
    <div class="table-toolbar"><v-chip-group v-model="filter" mandatory color="primary"><v-chip value="all">{{ t('views.common.all') }}</v-chip><v-chip value="active">{{ t('views.jobs.active') }}</v-chip><v-chip value="failed">{{ t('views.jobs.failed') }}</v-chip></v-chip-group><span class="text-caption muted">{{ t('views.jobs.count', { count: formatNumber(visibleJobs.length) }) }}</span></div>
    <v-table><thead><tr><th>{{ t('views.jobs.task') }}</th><th>{{ t('views.common.status') }}</th><th style="min-width: 150px">{{ t('views.jobs.progress') }}</th><th>{{ t('views.common.createdAt') }}</th><th /></tr></thead>
      <tbody><tr v-for="job in visibleJobs" :key="job.id">
        <td class="py-4"><router-link :to="`/sessions/${job.sessionId}`" class="font-weight-medium">{{ job.title || kindLabels[job.kind] || job.kind }}</router-link><div class="text-caption muted mt-1">{{ kindLabels[job.kind] || job.kind }}<span v-if="job.kind === 'Clip'"> · {{ job.mode === 'Fast' ? t('views.jobs.fast') : t('views.jobs.exact') }}</span></div><div v-if="job.error" class="text-caption text-error mt-2">{{ job.error }}</div></td>
        <td><StatusChip :status="job.state" /></td>
        <td><v-progress-linear :model-value="progress(job)" :indeterminate="['Running', 'Publishing'].includes(job.state) && job.progress === 0" color="primary" height="4" rounded /><div class="text-caption mono muted mt-2">{{ progress(job) }}%</div></td>
        <td class="text-caption muted text-no-wrap">{{ formatDate(job.createdAtUtc) }}</td>
        <td><div class="action-buttons"><v-btn v-if="['Queued', 'Running', 'Publishing'].includes(job.state)" variant="text" size="small" :disabled="job.cancelRequested" :loading="actionId === job.id" @click="act(job, 'cancel')">{{ job.cancelRequested ? t('views.jobs.cancelling') : t('views.common.cancel') }}</v-btn><v-btn v-if="['Failed', 'Canceled', 'Cancelled', 'Interrupted'].includes(job.state) && (job.kind === 'Clip' ? auth.canCreateClips : auth.isAdmin)" variant="text" size="small" color="primary" :loading="actionId === job.id" @click="act(job, 'retry')">{{ t('views.common.retry') }}</v-btn><template v-if="auth.canDownloadRecordings && job.state === 'Completed' && ['Repair', 'Merge'].includes(job.kind)"><v-btn v-for="(_, index) in job.outputRelativePaths" :key="index" :href="MediaAPI.downloadJobOutput(job.id, index)" variant="text" size="small" color="primary" prepend-icon="mdi-download">{{ job.outputRelativePaths.length > 1 ? t('views.jobs.downloadNumberedFile', { number: formatNumber(index + 1) }) : t('views.jobs.downloadFile') }}</v-btn></template></div></td>
      </tr></tbody>
    </v-table>
  </v-card>
  <EmptyState v-else-if="!busy" :title="t('views.jobs.empty')" icon="mdi-progress-clock" />
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { computed, ref } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAuthStore } from '@/stores/auth'
import { useAction } from '@/composables/useAction'
import { usePolling } from '@/composables/usePolling'
import type { MediaJob } from '@/models/media'
import { formatDate, formatNumber } from '@/utilities/format'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import StatusChip from '@/components/common/StatusChip.vue'

const { t } = useI18n()
const auth = useAuthStore()

const jobs = ref<MediaJob[]>([])
const filter = ref('all')
const actionId = ref('')
const kindLabels = computed<Record<string, string>>(() => ({ Clip: t('views.common.clips'), Repair: t('views.session.repair'), Merge: t('views.session.merge') }))
const { busy, error, run } = useAction()
const visibleJobs = computed(() => jobs.value.filter(job => filter.value === 'all' || (filter.value === 'active' ? ['Queued', 'Running', 'Publishing'].includes(job.state) : job.state === 'Failed')))
const progress = (job: MediaJob) => ['Completed', 'Succeeded'].includes(job.state) ? 100 : Math.round(Math.max(0, Math.min(100, job.progress)))
async function load(): Promise<void>
{
  await run(async () => { jobs.value = (await MediaAPI.jobs()).sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc)) })
}
async function act(job: MediaJob, action: 'cancel' | 'retry'): Promise<void>
{
  actionId.value = job.id
  await run(async () =>
  {
    await (action === 'cancel' ? MediaAPI.cancelJob(job.id) : MediaAPI.retryJob(job.id))
    jobs.value = await MediaAPI.jobs()
  })
  actionId.value = ''
}
usePolling(load, 4000)
</script>
