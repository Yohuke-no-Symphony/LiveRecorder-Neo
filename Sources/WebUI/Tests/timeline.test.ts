import { describe, expect, it } from 'vitest'
import { clampOffset, findGap, normalizeTimelineGaps, parseTimecode, playbackPosition, windowPosition } from '../src/utilities/timeline'
import { formatBytes, safeReturnPath } from '../src/utilities/format'

describe('完整直播时间轴', () =>
{
  it('窗口时间转换保留断流前的真实偏移', () =>
  {
    expect(windowPosition(137_500, 120_000)).toBe(17.5)
    expect(playbackPosition(17.5, 120_000, 300_000)).toBe(137_500)
    expect(playbackPosition(999, 120_000, 300_000)).toBe(300_000)
  })

  it('断流使用前闭后开区间，恢复第一帧不再算空档', () =>
  {
    const gaps = [{ startMs: 10_000, endMs: 20_000 }]
    expect(findGap(10_000, gaps)).toEqual(gaps[0])
    expect(findGap(19_999, gaps)).toEqual(gaps[0])
    expect(findGap(20_000, gaps)).toBeUndefined()
  })

  it('合并重叠空档并限制在场次范围内', () =>
  {
    expect(normalizeTimelineGaps([
      { startMs: 30, endMs: 120 }, { startMs: -10, endMs: 10 },
      { startMs: 8, endMs: 40 }, { startMs: 150, endMs: 170 },
    ], 100)).toEqual([{ startMs: 0, endMs: 100 }])
    expect(clampOffset(Number.NaN, 100)).toBe(0)
  })

  it('解析毫秒切片输入并拒绝错误时间', () =>
  {
    expect(parseTimecode('01:02:03.045')).toBe(3_723_045)
    expect(parseTimecode('12:03.5')).toBe(723_500)
    expect(parseTimecode('99:99')).toBeUndefined()
    expect(parseTimecode('-01:30')).toBeUndefined()
  })
})

describe('显示及登录返回路径', () =>
{
  it('拒绝站外和反斜线返回地址', () =>
  {
    expect(safeReturnPath('//evil.example')).toBe('/')
    expect(safeReturnPath('/\\evil.example')).toBe('/')
    expect(safeReturnPath('https://evil.example')).toBe('/')
    expect(safeReturnPath('/sessions/abc?at=30')).toBe('/sessions/abc?at=30')
  })

  it('显示空磁盘及大容量', () =>
  {
    expect(formatBytes(0)).toBe('0 B')
    expect(formatBytes(1024 ** 4)).toBe('1 TB')
  })
})
