import type { Entity } from './media'

export interface SessionBookmark extends Entity
{
  sessionId: string
  ownerId: string
  title: string
  offsetMs: number
}
