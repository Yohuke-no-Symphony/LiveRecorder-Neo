<template>
  <div ref="surface" class="echart-surface" :style="{ height: `${height}px` }" role="img" :aria-label="label"/>
</template>

<script setup lang="ts">
import {onMounted, onScopeDispose, ref, watch} from 'vue'
import {init, use, type EChartsCoreOption, type EChartsType} from 'echarts/core'
import {LineChart, BarChart, ScatterChart} from 'echarts/charts'
import {
  GridComponent,
  TooltipComponent,
  AxisPointerComponent,
  DataZoomComponent,
  MarkAreaComponent,
  MarkLineComponent
} from 'echarts/components'
import {CanvasRenderer} from 'echarts/renderers'

use([LineChart, BarChart, ScatterChart, GridComponent, TooltipComponent, AxisPointerComponent, DataZoomComponent, MarkAreaComponent, MarkLineComponent, CanvasRenderer])
const props = withDefaults(defineProps<{ option: EChartsCoreOption; label: string; height?: number }>(), {height: 540})
const emit = defineEmits<{ zoom: [fromMs: number, toMs: number]; point: [offsetMs: number] }>()
const surface = ref<HTMLDivElement>()
let chart: EChartsType | undefined
let resizeObserver: ResizeObserver | undefined
let themeObserver: MutationObserver | undefined

function render(): void {
  if (!chart || !surface.value) {
    return
  }
  const foreground = getComputedStyle(surface.value).getPropertyValue('--v-theme-on-surface').trim()
  const textColor = foreground ? `rgb(${foreground})` : '#8292a2'
  chart.setOption({...props.option, textStyle: {color: textColor}, backgroundColor: 'transparent'}, {notMerge: true})
}

onMounted(() => {
  chart = init(surface.value!, undefined, {renderer: 'canvas'})
  chart.on('datazoom', () => {
    const zoom = (chart!.getOption().dataZoom as {
      start?: number;
      end?: number;
      startValue?: number;
      endValue?: number
    }[] | undefined)?.[0]
    const axes = props.option.xAxis as { min?: number; max?: number }[]
    const maximum = axes?.[0]?.max
    if (zoom && typeof maximum === 'number') {
      emit('zoom', zoom.startValue ?? (zoom.start ?? 0) / 100 * maximum, zoom.endValue ?? (zoom.end ?? 100) / 100 * maximum)
    }
  })
  chart.on('click', event => {
    const value = event.value
    if (event.seriesType === 'scatter' && Array.isArray(value)) {
      const offset = Number(value[2])
      if (Number.isFinite(offset)) {
        emit('point', offset)
      }
    }
  })
  chart.getZr().on('click', event => {
    if (!Array.isArray(props.option.xAxis)) {
      return
    }
    const pixel = [event.offsetX, event.offsetY]
    for (let index = 0; index < props.option.xAxis.length; index++) {
      if (chart!.containPixel({gridIndex: index}, pixel)) {
        const offset = Number(chart!.convertFromPixel({xAxisIndex: index}, event.offsetX))
        if (Number.isFinite(offset)) {
          emit('point', offset)
        }
        break
      }
    }
  })
  resizeObserver = new ResizeObserver(() => chart?.resize())
  resizeObserver.observe(surface.value!)
  themeObserver = new MutationObserver(render)
  themeObserver.observe(surface.value!.closest('.v-application') ?? document.documentElement, {
    attributes: true,
    attributeFilter: ['class', 'style']
  })
  render()
})
watch(() => props.option, render)
onScopeDispose(() => {
  resizeObserver?.disconnect()
  themeObserver?.disconnect()
  chart?.dispose()
})
</script>

<style scoped>.echart-surface {
  width: 100%;
  min-width: 0;
}</style>
