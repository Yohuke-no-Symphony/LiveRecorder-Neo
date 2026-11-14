import { computed, toValue, type MaybeRefOrGetter } from 'vue'
import { useAuthStore } from '@/stores/auth'
import { useAiAnalysis } from './useAiAnalysis'

export function useAiLiveMarkers(sessionId: MaybeRefOrGetter<string>, ended: MaybeRefOrGetter<boolean>, active: MaybeRefOrGetter<boolean> = true)
{
  const auth = useAuthStore()
  const scope = computed(() => JSON.stringify([auth.user?.id, auth.user?.role, auth.user?.status, [...(auth.user?.permissions ?? [])].sort()]))
  const analysis = useAiAnalysis(sessionId, ended, {
    purpose: 'LiveMarkers', active: () => auth.canViewRecordings && toValue(active),
    canGenerate: () => auth.canGenerateLiveMarkers, scope,
  })
  return { ...analysis, scope }
}
