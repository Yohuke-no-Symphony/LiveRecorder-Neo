const AVATAR_COLORS = [
  '#B45309', '#B91C1C', '#BE185D', '#A21CAF', '#7E22CE', '#4338CA',
  '#1D4ED8', '#0369A1', '#0E7490', '#047857', '#4D7C0F',
] as const
const HASH_OFFSET = 2166136261
const HASH_PRIME = 16777619

export function userAvatarColor(name: string): string
{
  const normalizedName = name.trim()
  let hash = HASH_OFFSET
  for (let index = 0; index < normalizedName.length; index++)
  {
    hash = Math.imul(hash ^ normalizedName.charCodeAt(index), HASH_PRIME)
  }
  return AVATAR_COLORS[(hash >>> 0) % AVATAR_COLORS.length]
}
