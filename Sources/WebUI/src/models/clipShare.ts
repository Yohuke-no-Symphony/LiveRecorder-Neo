export interface ClipShareLink
{
  token: string | null
}

export interface SharedClip
{
  title: string
  durationMs: number
  bytes: number
  expiresAtUtc: string
}
