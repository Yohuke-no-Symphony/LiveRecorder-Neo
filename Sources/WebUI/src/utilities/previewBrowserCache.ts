import { getServerUrl } from '@/apis/ServerURL'
import { i18n } from '@/plugins/i18n'
import type { PreviewCacheMetadata } from '@/models/previewCacheMetadata'

const DATABASE_NAME = 'live-recorder-preview'
const DATABASE_VERSION = 1
const VALUE_STORE = 'values'
const METADATA_STORE = 'metadata'
const CACHE_RETENTION_MS = 7 * 24 * 60 * 60 * 1000
const CACHE_LIMIT_BYTES = 256 * 1024 * 1024
const CACHE_LIMIT_ENTRIES = 32
const DATABASE_OPEN_TIMEOUT_MS = 2000
let databasePromise: Promise<IDBDatabase | undefined> | undefined

export function previewCacheKey(kind: string, sessionId: string, scope: string, revision: string): string
{
  return JSON.stringify([DATABASE_VERSION, getServerUrl('/api/'), scope, kind, sessionId, revision, i18n.global.locale.value])
}

function openDatabase(): Promise<IDBDatabase | undefined>
{
  if (!globalThis.indexedDB)
  {
    return Promise.resolve(undefined)
  }
  if (databasePromise)
  {
    return databasePromise
  }
  const opening = new Promise<IDBDatabase | undefined>(resolve =>
  {
    let settled = false
    const finish = (database?: IDBDatabase) =>
    {
      if (settled)
      {
        database?.close()
        return
      }
      settled = true
      clearTimeout(timer)
      resolve(database)
    }
    const timer = setTimeout(() => finish(), DATABASE_OPEN_TIMEOUT_MS)
    try
    {
      const request = indexedDB.open(DATABASE_NAME, DATABASE_VERSION)
      request.onupgradeneeded = () =>
      {
        for (const name of [VALUE_STORE, METADATA_STORE])
        {
          if (!request.result.objectStoreNames.contains(name))
          {
            request.result.createObjectStore(name, { keyPath: 'key' })
          }
        }
      }
      request.onsuccess = () =>
      {
        const database = request.result
        database.onversionchange = () =>
        {
          database.close()
          databasePromise = undefined
        }
        finish(database)
      }
      request.onerror = () => finish()
      request.onblocked = () => finish()
    }
    catch
    {
      finish()
    }
  })
  databasePromise = opening
  void opening.then(database =>
  {
    if (!database && databasePromise === opening)
    {
      databasePromise = undefined
    }
  })
  return opening
}

function result<T>(request: IDBRequest<T>): Promise<T>
{
  return new Promise((resolve, reject) =>
  {
    request.onsuccess = () => resolve(request.result)
    request.onerror = () => reject(request.error)
  })
}

function completed(transaction: IDBTransaction): Promise<void>
{
  const completion = new Promise<void>((resolve, reject) =>
  {
    transaction.oncomplete = () => resolve()
    transaction.onabort = () => reject(transaction.error)
    transaction.onerror = () => reject(transaction.error)
  })
  void completion.catch(() => {})
  return completion
}

function abort(transaction: IDBTransaction | undefined): void
{
  try
  {
    transaction?.abort()
  }
  catch
  {
  }
}

function estimateBytes(value: unknown): number | undefined
{
  const serialized = JSON.stringify(value)
  return serialized === undefined ? undefined : serialized.length * 2
}

export async function readPreviewCache<T>(key: string): Promise<T | undefined>
{
  let transaction: IDBTransaction | undefined
  try
  {
    const database = await openDatabase()
    if (!database)
    {
      return undefined
    }
    transaction = database.transaction([VALUE_STORE, METADATA_STORE], 'readwrite')
    const completion = completed(transaction)
    const values = transaction.objectStore(VALUE_STORE)
    const metadata = transaction.objectStore(METADATA_STORE)
    const entry = await result(metadata.get(key)) as PreviewCacheMetadata | undefined
    if (!entry || entry.expiresAt <= Date.now())
    {
      values.delete(key)
      metadata.delete(key)
      await completion
      return undefined
    }
    const stored = await result(values.get(key)) as { key: string; value: T } | undefined
    if (stored)
    {
      metadata.put({ ...entry, accessedAt: Date.now() })
    }
    else
    {
      metadata.delete(key)
    }
    await completion
    return stored?.value
  }
  catch
  {
    abort(transaction)
    return undefined
  }
}

export async function writePreviewCache<T>(key: string, value: T): Promise<void>
{
  let transaction: IDBTransaction | undefined
  try
  {
    const database = await openDatabase()
    if (!database)
    {
      return
    }
    const bytes = estimateBytes(value)
    if (bytes === undefined)
    {
      return
    }
    if (bytes > CACHE_LIMIT_BYTES)
    {
      await removePreviewCache(key)
      return
    }
    transaction = database.transaction([VALUE_STORE, METADATA_STORE], 'readwrite')
    const completion = completed(transaction)
    const values = transaction.objectStore(VALUE_STORE)
    const metadata = transaction.objectStore(METADATA_STORE)
    const entries = await result(metadata.getAll()) as PreviewCacheMetadata[]
    const now = Date.now()
    const retained = entries.filter(entry =>
    {
      if (entry.key === key || entry.expiresAt <= now)
      {
        metadata.delete(entry.key)
        values.delete(entry.key)
        return false
      }
      return true
    }).sort((left, right) => left.accessedAt - right.accessedAt)
    let totalBytes = retained.reduce((sum, entry) => sum + entry.bytes, 0)
    while (retained.length >= CACHE_LIMIT_ENTRIES || totalBytes + bytes > CACHE_LIMIT_BYTES)
    {
      const removed = retained.shift()!
      totalBytes -= removed.bytes
      metadata.delete(removed.key)
      values.delete(removed.key)
    }
    metadata.put({ key, expiresAt: now + CACHE_RETENTION_MS, accessedAt: now, bytes } satisfies PreviewCacheMetadata)
    values.put({ key, value })
    await completion
  }
  catch
  {
    abort(transaction)
  }
}

export async function removePreviewCache(key: string): Promise<void>
{
  let transaction: IDBTransaction | undefined
  try
  {
    const database = await openDatabase()
    if (!database)
    {
      return
    }
    transaction = database.transaction([VALUE_STORE, METADATA_STORE], 'readwrite')
    const completion = completed(transaction)
    transaction.objectStore(VALUE_STORE).delete(key)
    transaction.objectStore(METADATA_STORE).delete(key)
    await completion
  }
  catch
  {
    abort(transaction)
  }
}
