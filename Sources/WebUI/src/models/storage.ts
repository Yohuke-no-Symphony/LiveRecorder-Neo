import type { StorageVolume } from './media'

export interface StorageUsageRow
{
  id: string
  name: string
  path: string
  recordingBytes: number
  derivedBytes?: number
  clipBytes: number
  trashBytes: number
  otherBytes: number
  totalBytes: number
}

export interface StorageRoomUsage extends StorageUsageRow
{
  roomId: number
  retainedInPlace?: boolean
}

export interface StorageUserUsage extends StorageUsageRow
{
  ownerId: string
}

export type StorageDirectoryKind = 'recordings' | 'derived' | 'clips' | 'trash' | 'cache' | 'work' | 'data' | 'other'

export interface StorageDirectoryUsage extends StorageUsageRow
{
  kind: StorageDirectoryKind
}

export interface StorageUsageReport
{
  scannedAtUtc: string
  isPartial?: boolean
  volumes: StorageVolume[]
  rooms: StorageRoomUsage[]
  users: StorageUserUsage[]
  directories: StorageDirectoryUsage[]
}

export type CacheCleanupCategory = 'playback' | 'artwork' | 'analyticsContributions'

export interface CacheCleanupOptions
{
  categories: CacheCleanupCategory[]
  minimumUnusedMinutes: number
}

export interface CacheCleanupCategoryResult
{
  category: CacheCleanupCategory
  deletedFiles: number
  deletedBytes: number
  skippedEntries: number
  failedEntries: number
}

export interface CacheCleanupResult
{
  categories: CacheCleanupCategoryResult[]
  deletedFiles: number
  deletedBytes: number
  skippedEntries: number
  failedEntries: number
}
