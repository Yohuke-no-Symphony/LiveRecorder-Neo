import { createI18n } from 'vue-i18n'
import { en, ja, zhHans } from 'vuetify/locale'
import { DEFAULT_LANGUAGE } from '@/localization/languages'
import { coreZh, coreEn, coreJa } from '@/localization/messages/core'
import { viewsZh, viewsEn, viewsJa } from '@/localization/messages/views'
import { componentsZh, componentsEn, componentsJa } from '@/localization/messages/components'
import { shellZh, shellEn, shellJa } from '@/localization/messages/shell'

export const i18n = createI18n({
  legacy: false,
  locale: DEFAULT_LANGUAGE as string,
  fallbackLocale: DEFAULT_LANGUAGE,
  messages: {
    'zh-CN': { core: coreZh, views: viewsZh, components: componentsZh, shell: shellZh, $vuetify: zhHans },
    'en-US': { core: coreEn, views: viewsEn, components: componentsEn, shell: shellEn, $vuetify: en },
    'ja-JP': { core: coreJa, views: viewsJa, components: componentsJa, shell: shellJa, $vuetify: ja },
  },
})
