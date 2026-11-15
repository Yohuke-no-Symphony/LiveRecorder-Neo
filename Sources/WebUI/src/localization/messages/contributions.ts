import type { MessageSchema } from '@/localization/types'

export const contributionsZh = {
  title: '付费用户总结', categories: '赠送类型', views: '查看方式', details: '逐条明细', grouped: '合并统计',
  records: '发送记录', senders: '发送用户', quantity: '赠送数量', amount: '流水', battery: '红包电池',
  quote: '报价', unknown: '未知', unknownAmounts: '{count} 条记录金额未知',
  range: '{from} — {to}', loading: '正在读取赠送记录…', loadFailed: '付费用户总结加载失败',
  empty: '此时段暂无此类赠送记录', retry: '重试', previous: '上一页', next: '下一页',
  page: '{from}–{to} / 共 {total} 条', groupedPage: '{from}–{to} / 共 {total} 位用户',
  allLoaded: '已显示全部记录', allGroupsLoaded: '已显示全部用户',
  count: '{count} 次', amountUnknown: '金额未知', freeGift: '银瓜子礼物',
  timeline: '赠送时间分布',
  pinRange: '固定时间选区', unpinRange: '取消固定时间选区',
  knownTotal: '已知总流水', projects: '礼物与大航海明细', emptyUsers: '此时段暂无赠送用户',
}

export const contributionsEn: MessageSchema<typeof contributionsZh> = {
  title: 'Paying user summary', categories: 'Contribution type', views: 'View', details: 'Individual records', grouped: 'Grouped totals',
  records: 'Contributions', senders: 'Senders', quantity: 'Quantity', amount: 'Revenue', battery: 'Red packet batteries',
  quote: 'Quoted value', unknown: 'Unknown', unknownAmounts: '{count} records have unknown amounts',
  range: '{from} — {to}', loading: 'Loading contributions…', loadFailed: 'Failed to load paying user summary',
  empty: 'No contributions of this type in this range', retry: 'Retry', previous: 'Previous', next: 'Next',
  page: '{from}–{to} of {total} records', groupedPage: '{from}–{to} of {total} users',
  allLoaded: 'All records are shown', allGroupsLoaded: 'All users are shown',
  count: '{count} contributions', amountUnknown: 'Unknown amount', freeGift: 'Silver-seed gift',
  timeline: 'Contribution timeline',
  pinRange: 'Pin time range', unpinRange: 'Unpin time range',
  knownTotal: 'Known total revenue', projects: 'Gift and membership details', emptyUsers: 'No senders in this time range',
}

export const contributionsJa: MessageSchema<typeof contributionsZh> = {
  title: '課金ユーザーのまとめ', categories: '贈り物の種類', views: '表示方法', details: '個別の明細', grouped: 'まとめて集計',
  records: '送信記録', senders: '送信ユーザー', quantity: '贈った数量', amount: '売上', battery: 'お年玉の電池',
  quote: '表示価格', unknown: '不明', unknownAmounts: '{count} 件の金額が不明です',
  range: '{from} — {to}', loading: '贈り物を読み込み中…', loadFailed: '課金ユーザーのまとめを読み込めませんでした',
  empty: 'この時間帯には該当する贈り物がありません', retry: '再試行', previous: '前のページ', next: '次のページ',
  page: '全 {total} 件中 {from}–{to} 件', groupedPage: '全 {total} 人中 {from}–{to} 人',
  allLoaded: 'すべての記録を表示しています', allGroupsLoaded: 'すべてのユーザーを表示しています',
  count: '{count} 回', amountUnknown: '金額不明', freeGift: '銀瓜子のギフト',
  timeline: '贈り物の時間分布',
  pinRange: '時間範囲を固定', unpinRange: '時間範囲の固定を解除',
  knownTotal: '確認済み売上合計', projects: 'ギフトと大航海の明細', emptyUsers: 'この時間帯には贈り物の送信者がいません',
}
