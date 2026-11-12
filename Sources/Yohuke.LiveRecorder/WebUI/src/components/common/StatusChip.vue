<template>
  <v-chip :color="appearance.color" size="small" :prepend-icon="appearance.icon">{{ appearance.label }}</v-chip>
</template>

<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{ status: string }>()
const definitions: Record<string, { label: string; color: string; icon?: string }> = {
  recording: { label: '录制中', color: 'error', icon: 'mdi-record' },
  live: { label: '直播中', color: 'error', icon: 'mdi-record' },
  monitoring: { label: '监听中', color: 'success' },
  waiting: { label: '等待开播', color: 'secondary' },
  paused: { label: '已暂停', color: 'warning' },
  suspended: { label: '已暂停', color: 'warning' },
  reconnecting: { label: '重连中', color: 'warning' },
  error: { label: '异常', color: 'error' },
  stopped: { label: '已停止', color: 'secondary' },
  offline: { label: '未开播', color: 'secondary' },
  queued: { label: '排队中', color: 'secondary', icon: 'mdi-clock-outline' },
  pending: { label: '待审核', color: 'warning' },
  running: { label: '处理中', color: 'info' },
  publishing: { label: '发布中', color: 'info' },
  completed: { label: '已完成', color: 'success' },
  succeeded: { label: '已完成', color: 'success' },
  ready: { label: '可播放', color: 'success' },
  failed: { label: '失败', color: 'error' },
  interrupted: { label: '已中断', color: 'warning' },
  canceled: { label: '已取消', color: 'secondary' },
  cancelled: { label: '已取消', color: 'secondary' },
  approved: { label: '已通过', color: 'success' },
  rejected: { label: '已拒绝', color: 'error' },
  disabled: { label: '已禁用', color: 'secondary' },
  expired: { label: '已过期', color: 'warning' },
  valid: { label: '有效', color: 'success' },
  active: { label: '有效', color: 'success' },
  invalid: { label: '登录失效', color: 'error' },
  needslogin: { label: '需重新登录', color: 'warning' },
  trashed: { label: '回收站', color: 'secondary' },
}
const appearance = computed(() => definitions[props.status.toLowerCase()] ?? { label: props.status, color: 'secondary' })
</script>
