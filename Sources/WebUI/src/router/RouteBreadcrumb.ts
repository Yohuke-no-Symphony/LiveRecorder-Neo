import type { shellZh } from '@/localization/messages/shell'

export interface RouteBreadcrumb
{
  titleKey: `shell.nav.${keyof typeof shellZh.nav}`
  to?: string
}
