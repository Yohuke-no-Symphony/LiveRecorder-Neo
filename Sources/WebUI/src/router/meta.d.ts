import 'vue-router'
import type { RouteBreadcrumb } from './RouteBreadcrumb'

declare module 'vue-router'
{
  interface RouteMeta
  {
    breadcrumbs?: readonly RouteBreadcrumb[]
  }
}
