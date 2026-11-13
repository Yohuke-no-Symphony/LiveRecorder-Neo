import { onBeforeUnmount, ref, toValue, watch, type MaybeRefOrGetter } from 'vue'
import { useI18n } from 'vue-i18n'
import { MediaAPI } from '@/apis/MediaAPI'
import type { SessionBookmark } from '@/models/sessionBookmark'

export function useSessionBookmarks(sessionId: MaybeRefOrGetter<string>, enabled: MaybeRefOrGetter<boolean>)
{
  const { t } = useI18n()
  const bookmarks = ref<SessionBookmark[]>([])
  const loading = ref(false)
  const saving = ref(false)
  const error = ref('')
  let scope = 0
  let readSequence = 0
  let controller: AbortController | undefined
  let disposed = false
  let hasSnapshot = false

  function cancelRead(): void
  {
    readSequence++
    controller?.abort()
    controller = undefined
    loading.value = false
  }

  function sortBookmarks(): void
  {
    bookmarks.value.sort((left, right) => left.offsetMs - right.offsetMs || left.id.localeCompare(right.id))
  }

  async function refresh(): Promise<void>
  {
    if (disposed || !toValue(enabled) || saving.value)
    {
      return
    }
    cancelRead()
    const sequence = readSequence
    const activeScope = scope
    controller = new AbortController()
    const signal = controller.signal
    loading.value = true
    error.value = ''
    try
    {
      const result = await MediaAPI.bookmarks(toValue(sessionId), signal)
      if (!disposed && !signal.aborted && activeScope === scope && sequence === readSequence)
      {
        bookmarks.value = result
        sortBookmarks()
        hasSnapshot = true
      }
    }
    catch (value)
    {
      if (!disposed && !signal.aborted && activeScope === scope && sequence === readSequence)
      {
        error.value = value instanceof Error ? value.message : t('components.bookmarks.loadFailed')
      }
    }
    finally
    {
      if (!disposed && activeScope === scope && sequence === readSequence)
      {
        loading.value = false
      }
    }
  }

  async function mutate<T>(action: (id: string) => Promise<T>, apply: (result: T) => void): Promise<T | undefined>
  {
    if (disposed || !toValue(enabled) || saving.value)
    {
      return undefined
    }
    const activeScope = scope
    const id = toValue(sessionId)
    let reloadAfterSave = false
    cancelRead()
    saving.value = true
    error.value = ''
    try
    {
      const result = await action(id)
      if (!disposed && activeScope === scope)
      {
        apply(result)
        reloadAfterSave = !hasSnapshot
        return result
      }
    }
    catch (value)
    {
      if (!disposed && activeScope === scope)
      {
        error.value = value instanceof Error ? value.message : t('components.common.operationFailed')
      }
    }
    finally
    {
      if (!disposed && activeScope === scope)
      {
        saving.value = false
        if (reloadAfterSave) { void refresh() }
      }
    }
    return undefined
  }

  function applyBookmark(bookmark: SessionBookmark): void
  {
    bookmarks.value = [...bookmarks.value.filter(item => item.id !== bookmark.id), bookmark]
    sortBookmarks()
  }

  function create(title: string, offsetMs: number): Promise<SessionBookmark | undefined>
  {
    return mutate(id => MediaAPI.createBookmark(id, { title, offsetMs }), applyBookmark)
  }

  function update(bookmarkId: string, title: string, offsetMs: number): Promise<SessionBookmark | undefined>
  {
    return mutate(id => MediaAPI.updateBookmark(id, bookmarkId, { title, offsetMs }), applyBookmark)
  }

  function remove(bookmarkId: string): Promise<boolean | undefined>
  {
    return mutate(async id =>
    {
      await MediaAPI.deleteBookmark(id, bookmarkId)
      return true
    }, () => { bookmarks.value = bookmarks.value.filter(item => item.id !== bookmarkId) })
  }

  watch([() => toValue(sessionId), () => toValue(enabled)], () =>
  {
    scope++
    cancelRead()
    bookmarks.value = []
    hasSnapshot = false
    saving.value = false
    error.value = ''
    void refresh()
  }, { immediate: true })

  onBeforeUnmount(() =>
  {
    disposed = true
    scope++
    cancelRead()
  })

  return { bookmarks, loading, saving, error, refresh, create, update, remove, clearError: () => { error.value = '' } }
}
