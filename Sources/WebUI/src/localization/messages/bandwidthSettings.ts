export const bandwidthSettingsZh = {
  title: '带宽', userLimitTitle: '用户下载限速', userLimit: '每账号下载上限（MiB/s）',
  unlimited: '0 表示不限速',
  smartTitle: '智能限速', smartEnabled: '启用智能限速', total: '总可用带宽（Mbps）',
  guarantee: '下载保底比例（%）',
  invalidRate: '请输入非负的有效速率；正数换算后必须至少为 1 字节/秒，且不能超过安全数值范围',
  invalidTotal: '启用智能限速时，总可用带宽必须大于 0',
  invalidGuarantee: '下载保底比例必须为 0 至 100 的整数',
  invalidSettings: '请检查带宽设置：速率需为有效非负数，智能限速需填写正的总带宽，下载保底需为 0 至 100 的整数',
}

export const bandwidthSettingsEn = {
  title: 'Bandwidth', userLimitTitle: 'User download limit', userLimit: 'Download limit per account (MiB/s)',
  unlimited: '0 means unlimited',
  smartTitle: 'Smart limiting', smartEnabled: 'Enable smart limiting', total: 'Total available bandwidth (Mbps)',
  guarantee: 'Guaranteed download share (%)',
  invalidRate: 'Enter a valid nonnegative rate; a positive value must convert to at least 1 byte/second and stay within the safe numeric range',
  invalidTotal: 'Total available bandwidth must be greater than 0 when smart limiting is enabled',
  invalidGuarantee: 'The guaranteed download share must be an integer from 0 to 100',
  invalidSettings: 'Check the bandwidth settings: rates must be valid nonnegative numbers, smart limiting requires a positive total bandwidth, and the download share must be an integer from 0 to 100',
}

export const bandwidthSettingsJa = {
  title: '帯域幅', userLimitTitle: 'ユーザーのダウンロード制限', userLimit: 'アカウントごとのダウンロード上限（MiB/s）',
  unlimited: '0 は無制限',
  smartTitle: '帯域幅の自動調整', smartEnabled: '帯域幅の自動調整を有効にする', total: '利用可能な総帯域幅（Mbps）',
  guarantee: 'ダウンロードの最低保証割合（%）',
  invalidRate: '0 以上の有効な速度を入力してください。正の値は毎秒 1 バイト以上に換算でき、安全な数値範囲内である必要があります',
  invalidTotal: '自動調整を有効にする場合、総帯域幅は 0 より大きい値が必要です',
  invalidGuarantee: 'ダウンロードの最低保証割合は 0～100 の整数で入力してください',
  invalidSettings: '帯域幅の設定を確認してください。速度は 0 以上の有効な数値、自動調整の総帯域幅は正の値、最低保証割合は 0～100 の整数が必要です',
}
