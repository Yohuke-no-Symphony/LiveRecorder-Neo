import { computed } from 'vue'
import { TranscodingAPI } from '@/apis/TranscodingAPI'
import { useAuthStore } from '@/stores/auth'
import { MAX_WORKER_LOG_ENTRIES, useWorkerLogs } from './useWorkerLogs'

export const MAX_TRANSCODING_LOG_ENTRIES = MAX_WORKER_LOG_ENTRIES

export function useTranscodingLogs(workerId: () => string)
{
  const auth = useAuthStore()
  const identity = computed(() => workerId() && auth.isAdmin
    ? JSON.stringify([workerId(), auth.user?.id, auth.user?.status, auth.isAdmin]) : null)
  return useWorkerLogs(() => identity.value, (after, signal) => TranscodingAPI.workerLogs(workerId(), after, signal))
}
