export interface BandwidthSettings
{
  userDownloadLimitBytesPerSecond: number
  smartLimitEnabled: boolean
  totalBytesPerSecond: number
  downloadGuaranteedPercent: number
}

export const BYTES_PER_MIB = 1024 ** 2
export const BYTES_PER_MEGABIT = 1000000 / 8

export function normalizeBandwidthSettings(value?: Partial<BandwidthSettings> | null): BandwidthSettings
{
  return {
    userDownloadLimitBytesPerSecond: value?.userDownloadLimitBytesPerSecond ?? 0,
    smartLimitEnabled: value?.smartLimitEnabled ?? false,
    totalBytesPerSecond: value?.totalBytesPerSecond ?? 0,
    downloadGuaranteedPercent: value?.downloadGuaranteedPercent ?? 10,
  }
}

export function bandwidthInputBytes(value: string | number | null, bytesPerUnit: number): number
{
  if (value === null || typeof value === 'string' && !value.trim())
  {
    return Number.NaN
  }
  const amount = Number(value)
  const bytes = Math.round(amount * bytesPerUnit)
  return Number.isFinite(amount) && amount >= 0 && (amount === 0 || bytes > 0) ? bytes : Number.NaN
}

export function isValidBandwidthRate(value: number): boolean
{
  return Number.isSafeInteger(value) && value >= 0
}

export function isValidDownloadGuaranteedPercent(value: number): boolean
{
  return Number.isInteger(value) && value >= 0 && value <= 100
}

export function isValidBandwidthSettings(settings: BandwidthSettings): boolean
{
  return typeof settings.smartLimitEnabled === 'boolean' &&
    isValidBandwidthRate(settings.userDownloadLimitBytesPerSecond) &&
    isValidBandwidthRate(settings.totalBytesPerSecond) &&
    (!settings.smartLimitEnabled || settings.totalBytesPerSecond > 0) &&
    isValidDownloadGuaranteedPercent(settings.downloadGuaranteedPercent)
}
