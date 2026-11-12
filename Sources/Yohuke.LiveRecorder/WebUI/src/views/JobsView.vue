<template>
  <PageHeader title="后台任务"><v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="busy" @click="load">刷新</v-btn></PageHeader>
  <v-alert v-if="error" type="error" class="mb-6">{{ error }}</v-alert>
  <v-card v-if="jobs.length">
    <div class="table-toolbar"><v-chip-group v-model="filter" mandatory color="primary"><v-chip value="all">全部</v-chip><v-chip value="active">进行中</v-chip><v-chip value="failed">失败</v-chip></v-chip-group><span class="text-caption muted">{{ visibleJobs.length }} 项</span></div>
    <v-table><thead><tr><th>任务</th><th>状态</th><th style="min-width: 150px">进度</th><th>创建时间</th><th /></tr></thead>
      <tbody><tr v-for="job in visibleJobs" :key="job.id">
        <td class="py-4"><router-link :to="`/sessions/${job.sessionId}`" class="font-weight-medium">{{ job.title || kindLabels[job.kind] || job.kind }}</router-link><div class="text-caption muted mt-1">{{ kindLabels[job.kind] || job.kind }}<span v-if="job.kind === 'Clip'"> · {{ job.mode === 'Fast' ? '快速' : '精确' }}</span></div><div v-if="job.error" class="text-caption text-error mt-2">{{ job.error }}</div></td>
        <td><StatusChip :status="job.state" /></td>
        <td><v-progress-linear :model-value="progress(job)" :indeterminate="['Running', 'Publishing'].includes(job.state) && job.progress === 0" color="primary" height="4" rounded /><div class="text-caption mono muted mt-2">{{ progress(job) }}%</div></td>
        <td class="text-caption muted text-no-wrap">{{ formatDate(job.createdAtUtc) }}</td>
        <td><div class="action-buttons"><v-btn v-if="['Queued', 'Running', 'Publishing'].includes(job.state)" variant="text" size="small" :disabled="job.cancelRequested" :loading="actionId === job.id" @click="act(job, 'cancel')">{{ job.cancelRequested ? '取消中' : '取消' }}</v-btn><v-btn v-if="['Failed', 'Canceled', 'Cancelled', 'Interrupted'].includes(job.state)" variant="text" size="small" color="primary" :loading="actionId === job.id" @click="act(job, 'retry')">重试</v-btn><template v-if="job.state === 'Completed' && ['Repair', 'Merge'].includes(job.kind)"><v-btn v-for="(_, index) in job.outputRelativePaths" :key="index" :href="MediaAPI.downloadJobOutput(job.id, index)" variant="text" size="small" color="primary" prepend-icon="mdi-download">{{ job.outputRelativePaths.length > 1 ? `下载文件 ${index + 1}` : '下载文件' }}</v-btn></template></div></td>
      </tr></tbody>
    </v-table>
  </v-card>
  <EmptyState v-else-if="!busy" title="暂无后台任务" icon="mdi-progress-clock" />
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { MediaAPI } from '@/apis/MediaAPI'
import { useAction } from '@/composables/useAction'
import { usePolling } from '@/composables/usePolling'
import type { MediaJob } from '@/models/media'
import { formatDate } from '@/utilities/format'
import PageHeader from '@/components/common/PageHeader.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import StatusChip from '@/components/common/StatusChip.vue'

const jobs = ref<MediaJob[]>([])
const filter = ref('all')
const actionId = ref('')
const kindLabels: Record<string, string> = { Clip: '切片', Repair: '修复时间码', Merge: '合并片段' }
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
