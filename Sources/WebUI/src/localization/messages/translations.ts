import type { MessageSchema } from '@/localization/types'

export const translationsZh = {
  tab: '同传', title: '同传', refresh: '刷新同传', settings: '同传设置',
  loading: '正在加载同传弹幕', empty: '没有匹配的同传弹幕', loadFailed: '同传弹幕加载失败，请刷新重试。',
  jump: '跳转到 {time}：{message}',
}

export const translationsEn: MessageSchema<typeof translationsZh> = {
  tab: 'Translations', title: 'Translations', refresh: 'Refresh translations', settings: 'Translation settings',
  loading: 'Loading translated chat', empty: 'No matching translated chat', loadFailed: 'Failed to load translated chat. Refresh to try again.',
  jump: 'Jump to {time}: {message}',
}

export const translationsJa: MessageSchema<typeof translationsZh> = {
  tab: '同時通訳', title: '同時通訳', refresh: '同時通訳を更新', settings: '同時通訳の設定',
  loading: '同時通訳コメントを読み込み中', empty: '一致する同時通訳コメントはありません', loadFailed: '同時通訳コメントを読み込めませんでした。更新して再試行してください。',
  jump: '{time} に移動：{message}',
}
