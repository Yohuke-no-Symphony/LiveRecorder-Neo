// @vitest-environment jsdom
import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { defineComponent, h } from 'vue'
import { init, setPlatformAPI, use, type EChartsCoreOption } from 'echarts/core'
import { SVGRenderer } from 'echarts/renderers'
import { LineChart, BarChart } from 'echarts/charts'
import { GridComponent, TooltipComponent, AxisPointerComponent, DataZoomComponent, MarkAreaComponent, MarkLineComponent } from 'echarts/components'
import RelationshipCharts from '../src/components/analytics/RelationshipCharts.vue'
import EChartSurface from '../src/components/analytics/EChartSurface.vue'
import { i18n } from '../src/plugins/i18n'
import type { Analytics } from '../src/models/media'

use([SVGRenderer, LineChart, BarChart, GridComponent, TooltipComponent, AxisPointerComponent, DataZoomComponent, MarkAreaComponent, MarkLineComponent])
setPlatformAPI({ measureText: text => ({ width: String(text).length * 7 }) })
const passthrough = defineComponent({ setup: (_, { slots }) => () => h('div', slots.default?.()) })

describe('shared timeline pixel alignment', () =>
{
  it.each([400, 1280])('keeps all three time axes aligned at width %s, before and after zoom', async width =>
  {
    const durationMs = 2603123.45
    const report = {
      online: { intervals: [{ fromMs: 0, toMs: durationMs, count: 12345 }] },
      buckets: Array.from({ length: 44 }, (_, index) => ({ offsetMs: index * 60000, danmuCount: index * 13, superChatCount: 0, superChatAmount: 0 })),
    } as unknown as Analytics
    const wrapper = mount(RelationshipCharts, { props: { sessionId: 'session', report, durationMs }, global: {
      plugins: [i18n], stubs: { EChartSurface: true, CardHeader: true, VBtn: passthrough, VTooltip: true },
    } })
    const chart = init(null, undefined, { renderer: 'svg', ssr: true, width, height: 540 })
    try
    {
      for (const range of [undefined, { fromMs: 12234.5, toMs: 2198765.4 }])
      {
        await wrapper.setProps({ range })
        chart.setOption(wrapper.findAllComponents(EChartSurface)[0]!.props('option') as EChartsCoreOption, { notMerge: true })
        const from = range?.fromMs ?? 0
        const to = range?.toMs ?? durationMs
        for (const time of [from, (from + to) / 2, to])
        {
          const expected = Number(chart.convertToPixel({ xAxisIndex: 0 }, time))
          expect(Number(chart.convertToPixel({ xAxisIndex: 1 }, time))).toBeCloseTo(expected, 8)
          expect(Number(chart.convertToPixel({ xAxisIndex: 2 }, time))).toBeCloseTo(expected, 8)
        }
      }
    }
    finally
    {
      chart.dispose()
      wrapper.unmount()
    }
  })
})
