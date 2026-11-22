import type { Clip } from './media'

export interface ManagedClip extends Omit<Clip, 'jobId' | 'isTrashed'>
{
  ownerDisplayName: string
  ownerAvatarUrl?: string | null
}

export interface ManagedClipPage
{
  items: ManagedClip[]
  total: number
}

export interface ClipManagementQuery
{
  page: number
  pageSize: number
  sort?: string
  direction?: 'asc' | 'desc'
}

export interface ClipTrashResult
{
  clipId: string
  success: boolean
  error?: string | null
}
