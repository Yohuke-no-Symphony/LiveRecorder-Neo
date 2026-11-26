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
}
