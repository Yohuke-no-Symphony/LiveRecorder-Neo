import { createI18n } from 'vue-i18n'
import { taskHistoryZh, taskHistoryEn, taskHistoryJa } from '@/localization/messages/taskHistory'
import { en, ja, zhHans } from 'vuetify/locale'
import { DEFAULT_LANGUAGE } from '@/localization/languages'
import { coreZh, coreEn, coreJa } from '@/localization/messages/core'
import { viewsZh, viewsEn, viewsJa } from '@/localization/messages/views'
import { componentsZh, componentsEn, componentsJa } from '@/localization/messages/components'
import { shellZh, shellEn, shellJa } from '@/localization/messages/shell'
import { liveMarkersZh, liveMarkersEn, liveMarkersJa } from '@/localization/messages/liveMarkers'
import { translationsZh, translationsEn, translationsJa } from '@/localization/messages/translations'
import { replayZh, replayEn, replayJa } from '@/localization/messages/replay'
import { aiSettingsZh, aiSettingsEn, aiSettingsJa } from '@/localization/messages/aiSettings'
import { transcodingZh, transcodingEn, transcodingJa } from '@/localization/messages/transcoding'
import { bandwidthSettingsZh, bandwidthSettingsEn, bandwidthSettingsJa } from '@/localization/messages/bandwidthSettings'

import { accountManagementZh, accountManagementEn, accountManagementJa } from '@/localization/messages/accountManagement'
import { passwordResetZh, passwordResetEn, passwordResetJa } from '@/localization/messages/passwordReset'
import { contributionsZh, contributionsEn, contributionsJa } from '@/localization/messages/contributions'
import { analyticsExportZh, analyticsExportEn, analyticsExportJa } from '@/localization/messages/analyticsExport'
import { sharingZh, sharingEn, sharingJa } from '@/localization/messages/sharing'
import { serverLogsZh, serverLogsEn, serverLogsJa } from '@/localization/messages/serverLogs'

export const i18n = createI18n({
  legacy: false,
  locale: DEFAULT_LANGUAGE as string,
  fallbackLocale: DEFAULT_LANGUAGE,
  messages: {
    'zh-CN': { taskHistory: taskHistoryZh, transcoding: transcodingZh, analyticsExport: analyticsExportZh, contributions: contributionsZh, bandwidthSettings: bandwidthSettingsZh, accountManagement: accountManagementZh, passwordReset: passwordResetZh, aiSettings: aiSettingsZh, replay: replayZh, core: coreZh, views: viewsZh, components: componentsZh, shell: shellZh, liveMarkers: liveMarkersZh, translations: translationsZh, sharing: sharingZh, serverLogs: serverLogsZh, $vuetify: zhHans },
    'en-US': { taskHistory: taskHistoryEn, transcoding: transcodingEn, analyticsExport: analyticsExportEn, contributions: contributionsEn, bandwidthSettings: bandwidthSettingsEn, accountManagement: accountManagementEn, passwordReset: passwordResetEn, aiSettings: aiSettingsEn, replay: replayEn, core: coreEn, views: viewsEn, components: componentsEn, shell: shellEn, liveMarkers: liveMarkersEn, translations: translationsEn, sharing: sharingEn, serverLogs: serverLogsEn, $vuetify: en },
    'ja-JP': { taskHistory: taskHistoryJa, transcoding: transcodingJa, analyticsExport: analyticsExportJa, contributions: contributionsJa, bandwidthSettings: bandwidthSettingsJa, accountManagement: accountManagementJa, passwordReset: passwordResetJa, aiSettings: aiSettingsJa, replay: replayJa, core: coreJa, views: viewsJa, components: componentsJa, shell: shellJa, liveMarkers: liveMarkersJa, translations: translationsJa, sharing: sharingJa, serverLogs: serverLogsJa, $vuetify: ja },
  },
})
