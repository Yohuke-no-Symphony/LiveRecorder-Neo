import { onBeforeUnmount, ref, shallowRef, toValue, watch, type MaybeRefOrGetter } from 'vue'
import { useI18n } from 'vue-i18n'
import { ApiError } from '@/apis/APIBase'
import { LiveMarkerAPI } from '@/apis/LiveMarkerAPI'
import type { LiveMarkerDetail, LiveMarkerFile, LiveMarkerUpload } from '@/models/liveMarkers'
import { previewCacheKey, readPreviewCache, writePreviewCache } from '@/utilities/previewBrowserCache'

export function useSessionLiveMarkers(sessionId: MaybeRefOrGetter<string>, active: MaybeRefOrGetter<boolean>, preferredFileId?: MaybeRefOrGetter<string | undefined>, scopeKey?: MaybeRefOrGetter<string>)
{
  const { t } = useI18n()
  const files = ref<LiveMarkerFile[]>([])
  const selectedId = ref('')
  const detail = shallowRef<LiveMarkerDetail>()
  const defaultTimeZoneId = ref('')
  const loadingFiles = ref(false)
  const loadingEntries = ref(false)
  const saving = ref(false)
  const error = ref('')
  const actionError = ref('')
  let scope = 0
  let listSequence = 0
  let entrySequence = 0
  let listController: AbortController | undefined
  let entryController: AbortController | undefined
  let disposed = false
  let requestedFileId = toValue(preferredFileId) ?? ''
  let requestedFileMissing = false
  let hasSnapshot = false
  let pendingRefresh = false
  let pendingDetails = false
  let loadingFile: LiveMarkerFile | undefined

  function cancelReads(): void
  {
    listSequence++
    entrySequence++
    listController?.abort()
    entryController?.abort()
    loadingFiles.value = false
    loadingEntries.value = false
    loadingFile = undefined
  }

  function sortFiles(values: LiveMarkerFile[]): LiveMarkerFile[]
  {
    return [...values].sort((left, right) => Date.parse(right.uploadedAtUtc) - Date.parse(left.uploadedAtUtc) || right.id.localeCompare(left.id))
  }

  function sameVersion(left: LiveMarkerFile | undefined, right: LiveMarkerFile | undefined): boolean
  {
    return !!left && !!right && left.id === right.id && left.calibrationMs === right.calibrationMs && left.revision === right.revision
  }

  function cacheKey(file: LiveMarkerFile): string | undefined
  {
    const identity = toValue(scopeKey)
    return identity && file.revision ? previewCacheKey('live-markers', toValue(sessionId), identity, JSON.stringify([file.id, file.revision])) : undefined
  }

  async function loadSelected(force = false, continueHidden = false): Promise<void>
  {
    const fileId = selectedId.value
    if (disposed || (!toValue(active) && !continueHidden) || saving.value || !fileId)
    {
      return
    }
    const file = files.value.find(item => item.id === fileId)
    if (!file)
    {
      return
    }
    const hasCurrentDetail = sameVersion(detail.value?.file, file)
    const loadingCurrentFile = loadingEntries.value && sameVersion(loadingFile, file)
    if (!force && (hasCurrentDetail || loadingCurrentFile))
    {
      return
    }
    entryController?.abort()
    const sequence = ++entrySequence
    const currentScope = scope
    entryController = new AbortController()
    const signal = entryController.signal
    loadingEntries.value = true
    loadingFile = file
    error.value = ''
    try
    {
      const key = cacheKey(file)
      if (!force && key)
      {
        const cached = await readPreviewCache<LiveMarkerDetail>(key)
        if (disposed || signal.aborted || currentScope !== scope || sequence !== entrySequence || selectedId.value !== fileId)
        {
          return
        }
        if (cached && sameVersion(cached.file, file) && Array.isArray(cached.entries))
        {
          detail.value = { ...cached, file }
          return
        }
      }
      const result = await LiveMarkerAPI.detail(toValue(sessionId), fileId, signal)
      if (!disposed && !signal.aborted && currentScope === scope && sequence === entrySequence && selectedId.value === fileId)
      {
        detail.value = { ...result, entries: [...result.entries].sort((left, right) => left.offsetMs - right.offsetMs || left.index - right.index) }
        files.value = files.value.map(file => file.id === fileId ? result.file : file)
        const resultKey = cacheKey(result.file)
        if (resultKey)
        {
          await writePreviewCache(resultKey, detail.value)
        }
      }
    }
    catch (value)
    {
      if (!disposed && !signal.aborted && currentScope === scope && sequence === entrySequence)
      {
        error.value = value instanceof Error ? value.message : t('liveMarkers.loadFailed')
        if (value instanceof ApiError && value.status === 404)
        {
          files.value = files.value.filter(file => file.id !== fileId)
          selectedId.value = files.value[0]?.id ?? ''
          detail.value = undefined
          if (selectedId.value) { void loadSelected(false, continueHidden) }
        }
      }
    }
    finally
    {
      if (!disposed && currentScope === scope && sequence === entrySequence)
      {
        loadingEntries.value = false
        loadingFile = undefined
      }
    }
  }

  async function refresh(forceDetails = true): Promise<void>
  {
    if (disposed)
    {
      return
    }
    pendingRefresh = true
    pendingDetails ||= forceDetails
    if (!toValue(active) || saving.value)
    {
      return
    }
    const reloadDetails = pendingDetails
    pendingRefresh = false
    pendingDetails = false
    listController?.abort()
    const sequence = ++listSequence
    const currentScope = scope
    listController = new AbortController()
    const signal = listController.signal
    loadingFiles.value = true
    error.value = requestedFileMissing ? t('liveMarkers.fileMissing') : ''
    try
    {
      const result = await LiveMarkerAPI.files(toValue(sessionId), signal)
      if (disposed || signal.aborted || currentScope !== scope || sequence !== listSequence)
      {
        return
      }
      files.value = sortFiles(result.files)
      defaultTimeZoneId.value = result.defaultTimeZoneId
      hasSnapshot = true
      const requested = files.value.find(file => file.id === requestedFileId)
      if (requestedFileId && !requested)
      {
        requestedFileId = ''
        requestedFileMissing = true
        selectedId.value = ''
        detail.value = undefined
        entrySequence++
        entryController?.abort()
        loadingEntries.value = false
        error.value = t('liveMarkers.fileMissing')
        return
      }
      const selected = requested ?? files.value.find(file => file.id === selectedId.value) ?? (requestedFileMissing ? undefined : files.value[0])
      if (requested) { requestedFileId = '' }
      if (selected)
      {
        requestedFileMissing = false
        error.value = ''
      }
      selectedId.value = selected?.id ?? ''
      if (!selected)
      {
        entrySequence++
        entryController?.abort()
        loadingEntries.value = false
        detail.value = undefined
      }
      else if (reloadDetails || !sameVersion(detail.value?.file, selected))
      {
        if (detail.value?.file.id !== selected.id) { detail.value = undefined }
        await loadSelected(reloadDetails, true)
      }
      else
      {
        detail.value = { ...detail.value!, file: selected }
      }
    }
    catch (value)
    {
      if (!disposed && !signal.aborted && currentScope === scope && sequence === listSequence)
      {
        pendingRefresh = true
        pendingDetails ||= reloadDetails
        error.value = value instanceof Error ? value.message : t('liveMarkers.loadFailed')
      }
    }
    finally
    {
      if (!disposed && currentScope === scope && sequence === listSequence)
      {
        loadingFiles.value = false
      }
    }
  }

  async function select(fileId: string): Promise<void>
  {
    if (saving.value || !files.value.some(file => file.id === fileId))
    {
      return
    }
    if (selectedId.value === fileId)
    {
      await loadSelected()
      return
    }
    selectedId.value = fileId
    requestedFileId = ''
    requestedFileMissing = false
    detail.value = undefined
    error.value = ''
    await loadSelected()
  }

  async function ensureLoaded(): Promise<void>
  {
    if (!toValue(active) || saving.value)
    {
      return
    }
    if (pendingRefresh)
    {
      await refresh(false)
    }
    else if (loadingFiles.value || loadingEntries.value)
    {
      return
    }
    else if (!hasSnapshot)
    {
      await refresh(false)
    }
    else
    {
      await loadSelected()
    }
  }

  async function reveal(fileId: string): Promise<void>
  {
    requestedFileId = fileId
    requestedFileMissing = false
    await refresh(false)
  }

  async function mutate<T>(action: (id: string) => Promise<T>, apply: (result: T) => void): Promise<T | undefined>
  {
    if (disposed || saving.value || !toValue(active))
    {
      return undefined
    }
    const currentScope = scope
    cancelReads()
    saving.value = true
    actionError.value = ''
    try
    {
      const result = await action(toValue(sessionId))
      if (!disposed && currentScope === scope)
      {
        apply(result)
        return result
      }
    }
    catch (value)
    {
      if (!disposed && currentScope === scope)
      {
        actionError.value = value instanceof Error ? value.message : t('components.common.operationFailed')
      }
    }
    finally
    {
      if (!disposed && currentScope === scope)
      {
        saving.value = false
        await refresh(false)
      }
    }
    return undefined
  }

  function upload(value: LiveMarkerUpload): Promise<LiveMarkerFile | undefined>
  {
    return mutate(id => LiveMarkerAPI.upload(id, value), file =>
    {
      files.value = sortFiles([...files.value.filter(item => item.id !== file.id), file])
      selectedId.value = file.id
      detail.value = undefined
    })
  }

  function align(fileId: string, calibrationMs: number): Promise<LiveMarkerFile | undefined>
  {
    return mutate(id => LiveMarkerAPI.align(id, fileId, calibrationMs), file =>
    {
      files.value = files.value.map(item => item.id === file.id ? file : item)
      if (selectedId.value === file.id) { detail.value = undefined }
    })
  }

  function remove(fileId: string): Promise<boolean | undefined>
  {
    return mutate(async id =>
    {
      try
      {
        await LiveMarkerAPI.remove(id, fileId)
      }
      catch (value)
      {
        if (!(value instanceof ApiError) || value.status !== 404) { throw value }
      }
      return true
    }, () =>
    {
      files.value = files.value.filter(file => file.id !== fileId)
      if (selectedId.value === fileId)
      {
        selectedId.value = files.value[0]?.id ?? ''
        detail.value = undefined
      }
    })
  }

  watch([() => toValue(sessionId), () => toValue(scopeKey)], () =>
  {
    scope++
    cancelReads()
    files.value = []
    selectedId.value = ''
    requestedFileId = toValue(preferredFileId) ?? ''
    requestedFileMissing = false
    detail.value = undefined
    defaultTimeZoneId.value = ''
    hasSnapshot = false
    pendingRefresh = false
    pendingDetails = false
    saving.value = false
    error.value = ''
    actionError.value = ''
    void ensureLoaded()
  }, { immediate: true, flush: 'sync' })

  watch(() => toValue(preferredFileId), value =>
  {
    requestedFileId = value ?? ''
    requestedFileMissing = false
    if (value) { void refresh(false) }
  })

  watch(() => toValue(active), value =>
  {
    if (value) { void ensureLoaded() }
  })
  onBeforeUnmount(() =>
  {
    disposed = true
    scope++
    cancelReads()
  })

  return { files, selectedId, detail, defaultTimeZoneId, loadingFiles, loadingEntries, saving, error, actionError, refresh, select, reveal, upload, align, remove, clearActionError: () => { actionError.value = '' } }
}
