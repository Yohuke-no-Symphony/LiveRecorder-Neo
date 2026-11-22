import type { RouteMeta } from 'vue-router'
import type { useAuthStore } from '@/stores/auth'

export function canAccessRoute(meta: RouteMeta, auth: ReturnType<typeof useAuthStore>): boolean
{
  return (!meta.admin || auth.isAdmin)
    && (!meta.permissions || meta.permissions.every(auth.hasPermission))
    && (!meta.anyPermission || meta.anyPermission.some(auth.hasPermission))
}
