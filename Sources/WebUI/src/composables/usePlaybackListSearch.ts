import { computed, ref, toValue, watch, type MaybeRefOrGetter } from 'vue'

export function usePlaybackListSearch(sessionId: MaybeRefOrGetter<string>)
{
  const searchOpen = ref(false)
  const searchText = ref('')
  const query = computed(() => searchOpen.value ? searchText.value.trim().toLowerCase() : '')
  const isSearching = computed(() => query.value.length > 0)

  function matchesSearch(...values: string[]): boolean
  {
    return !query.value || values.some(value => value.toLowerCase().includes(query.value))
  }

  function closeSearch(): void
  {
    searchOpen.value = false
    searchText.value = ''
  }

  watch(() => toValue(sessionId), closeSearch, { flush: 'sync' })

  return { searchOpen, searchText, isSearching, matchesSearch, closeSearch }
}
