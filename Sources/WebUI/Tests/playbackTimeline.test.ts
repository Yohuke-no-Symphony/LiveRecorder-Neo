// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h } from 'vue'
import { i18n } from '../src/plugins/i18n'
import type { SessionTimeline, TimelineMarkerGroup } from '../src/models/sessionTimeline'
import { groupTimelineMarkers, timelineHeatColumns } from '../src/utilities/playbackTimeline'
import PlaybackTimeline from '../src/components/player/PlaybackTimeline.vue'

const mounted: VueWrapper[] = []
const slider = defineComponent({ name: 'VSlider', props: ['modelValue', 'max'], emits: ['update:modelValue', 'start', 'end'], setup: () => () => h('div', { class: 'slider' }) })

function marker(patch: Partial<TimelineMarkerGroup> = {}): TimelineMarkerGroup
{
  return { offsetMs: 1000, endOffsetMs: 1000, kind: 'Gift', count: 1, sequence: 1, userName: '用户甲', text: '小花', amount: null, quantity: 1, ...patch }
}
function snapshot(): SessionTimeline
{
  return { durationMs: 10000, bucketWidthMs: 1000, revision: 'r1', heat: [{ offsetMs: 0, count: 2 }, { offsetMs: 5000, count: 8 }], markers: [marker({ offsetMs: 6000, endOffsetMs: 6500 }), marker({ offsetMs: 8000, endOffsetMs: 8000, kind: 'SuperChat', sequence: 2, text: '<img src=x onerror=alert(1)>', amount: 30 })] }
}
function mountTimeline(data = snapshot()): VueWrapper
{
  const wrapper = mount(PlaybackTimeline, { props: { modelValue: 2000, durationMs: 10000, gaps: [{ startMs: 3000, endMs: 4000 }], timeline: data }, global: { plugins: [i18n], stubs: { VSlider: slider } } })
  mounted.push(wrapper)
  return wrapper
}
beforeEach(() =>
{
  i18n.global.locale.value = 'zh-CN'
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} unobserve() {} })
})
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); vi.unstubAllGlobals() })

describe('时间轴密集事件投影', () =>
{
  it('相邻同类记录聚合，同时保留组首时间、摘要及数量', () =>
  {
    const groups = groupTimelineMarkers([marker({ offsetMs: 1100, endOffsetMs: 1400, count: 2, sequence: 2, amount: 5, quantity: 4 }), marker({ count: 3, amount: 2 }), marker({ kind: 'SuperChat', sequence: 3 })], 100000, 10)
    expect(groups).toHaveLength(2)
    const gift = groups.find(item => item.kind === 'Gift')!
    expect(gift).toMatchObject({ offsetMs: 1000, endOffsetMs: 1400, count: 5, sequence: 1, amount: 7, quantity: 5, userName: '用户甲' })
  })
  it('限制密集标记数量，并且不混合不同事件类型', () =>
  {
    const kinds = ['Gift', 'SuperChat', 'RedPocket', 'Guard'] as const
    const source = Array.from({ length: 4000 }, (_, index) => marker({ kind: kinds[index % 4]!, sequence: index + 1, offsetMs: index, endOffsetMs: index }))
    const groups = groupTimelineMarkers(source, 4000, 8)
    expect(groups.length).toBeLessThanOrEqual(32)
    expect(groups.reduce((sum, item) => sum + item.count, 0)).toBe(4000)
  })
  it('拒绝无效时间和空时间轴，末端标记仍可定位', () =>
  {
    expect(groupTimelineMarkers([marker()], 0, 20)).toEqual([])
    const groups = groupTimelineMarkers([marker({ offsetMs: Number.NaN }), marker({ offsetMs: -1 }), marker({ offsetMs: 10001 }), marker({ offsetMs: 10000, endOffsetMs: 20000 })], 10000, 20)
    expect(groups).toHaveLength(1)
    expect(groups[0]).toMatchObject({ offsetMs: 10000, endOffsetMs: 10000 })
  })
  it('热度只投影有效桶，限制末端宽度并避免NaN', () =>
  {
    const columns = timelineHeatColumns([{ offsetMs: 0, count: 2 }, { offsetMs: 9500, count: 8 }, { offsetMs: 4000, count: 0 }, { offsetMs: Number.NaN, count: 9 }], 2000, 10000)
    expect(columns).toHaveLength(2)
    expect(columns[1]!.strength).toBe(1)
    expect(columns[0]!.strength).toBeGreaterThan(0)
    expect(columns[0]!.strength).toBeLessThan(1)
    expect(columns[1]!.left + columns[1]!.width).toBe(100)
    expect(timelineHeatColumns([{ offsetMs: 0, count: 1 }], 1000, 0)).toEqual([])
  })
})

describe('播放器互动叠层', () =>
{
  it('悬停显示摘要，点击跳到对应记录，文本不解释为HTML', async () =>
  {
    const wrapper = mountTimeline()
    const target = wrapper.get('.timeline-marker[data-kind="SuperChat"]')
    await target.trigger('mouseenter')
    expect(wrapper.get('[role="tooltip"]').text()).toContain('<img src=x onerror=alert(1)>')
    expect(wrapper.get('[role="tooltip"]').text()).toContain('00:08')
    expect(wrapper.find('[onerror]').exists()).toBe(false)
    await target.trigger('click')
    expect(wrapper.emitted('seek')).toEqual([[8000]])
  })
  it('分类开关只隐藏对应标记，热度桶也能点击定位', async () =>
  {
    const wrapper = mountTimeline()
    await wrapper.get('.timeline-filter[data-kind="Gift"]').trigger('click')
    expect(wrapper.find('.timeline-marker[data-kind="Gift"]').exists()).toBe(false)
    expect(wrapper.find('.timeline-marker[data-kind="SuperChat"]').exists()).toBe(true)
    expect(wrapper.get('.timeline-filter[data-kind="Gift"]').attributes('aria-pressed')).toBe('false')
    await wrapper.findAll('.timeline-heat rect')[1]!.trigger('click')
    expect(wrapper.emitted('seek')).toEqual([[5000]])
  })
  it('直播快照变化后不会继续展示旧热度桶的悬停数据', async () =>
  {
    const wrapper = mountTimeline()
    await wrapper.findAll('.timeline-heat rect')[0]!.trigger('mouseenter')
    expect(wrapper.find('[role="tooltip"]').exists()).toBe(true)
    const updated = snapshot()
    updated.revision = 'r2'
    updated.heat[0]!.count = 99
    await wrapper.setProps({ timeline: updated })
    expect(wrapper.find('[role="tooltip"]').exists()).toBe(false)
    await wrapper.findAll('.timeline-heat rect')[0]!.trigger('mouseenter')
    expect(wrapper.get('[role="tooltip"]').text()).toContain('99')
  })

  it('保留进度条拖动事件与断流叠层', async () =>
  {
    const wrapper = mountTimeline()
    const input = wrapper.findComponent(slider)
    input.vm.$emit('start')
    input.vm.$emit('update:modelValue', 7000)
    input.vm.$emit('end')
    expect(wrapper.emitted('start')).toHaveLength(1)
    expect(wrapper.emitted('update:modelValue')).toEqual([[7000]])
    expect(wrapper.emitted('end')).toHaveLength(1)
    expect(wrapper.findAll('.timeline-gaps span')).toHaveLength(1)
  })
  it('加载错误保留已有记录，允许单独重试', async () =>
  {
    const wrapper = mountTimeline()
    await wrapper.setProps({ error: '暂时离线' })
    expect(wrapper.findAll('.timeline-marker')).toHaveLength(2)
    await wrapper.get('.timeline-retry').trigger('click')
    expect(wrapper.emitted('retry')).toHaveLength(1)
  })
})
