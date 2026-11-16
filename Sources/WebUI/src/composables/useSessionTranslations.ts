import { computed, type MaybeRefOrGetter } from 'vue'
import { useSessionEventSnapshot } from './useSessionEventSnapshot'

export function useSessionTranslations(sessionId: MaybeRefOrGetter<string>, active: MaybeRefOrGetter<boolean>, enabled: MaybeRefOrGetter<boolean>, isLive: MaybeRefOrGetter<boolean>, configurationRevision?: MaybeRefOrGetter<number | undefined>)
{
  const state = useSessionEventSnapshot({ sessionId, active, enabled, isLive, configurationRevision })
  const error = computed(() => state.error.value || state.translationError.value)
  return { entries: state.translations, loading: state.loading, hasSnapshot: state.hasSnapshot, error, refresh: () => state.refresh(!!state.translationError.value) }
}
