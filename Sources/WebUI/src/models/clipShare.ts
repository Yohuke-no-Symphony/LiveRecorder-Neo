import type { PublicUserProfile } from './auth'

export type ClipShareVisibility = 'public' | 'private'

export interface ClipShareLink
{
  token: string | null
  visibility?: ClipShareVisibility | null
}

export interface ClipShareSettings
{
  token: string | null
  visibility: ClipShareVisibility
  password?: string
}

export interface SharedClip
{
  title: string
  durationMs: number
  bytes: number
  expiresAtUtc: string
  owner?: PublicUserProfile | null
}

export interface PublicClipShare extends Omit<SharedClip, 'owner'>
{
  id: string
  token: string
  createdAtUtc: string
}

export interface PublicClipShareCursor
{
  beforeUtc: string
  beforeId: string
}

export interface PublicClipSharePage
{
  items: PublicClipShare[]
  nextCursor: PublicClipShareCursor | null
}
