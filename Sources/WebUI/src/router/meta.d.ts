import 'vue-router'
import type { RouteBreadcrumb } from './RouteBreadcrumb'

declare module 'vue-router'
{
  interface RouteMeta
  {
    admin?: boolean
    public?: boolean
    fillViewport?: boolean
    permissions?: readonly string[]
    anyPermission?: readonly string[]
    breadcrumbs?: readonly RouteBreadcrumb[]
  }
}
