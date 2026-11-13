<template>
  <img v-if="source" v-bind="$attrs" :src="source" :alt="alt" loading="lazy" decoding="async" referrerpolicy="no-referrer" @error="onError" />
  <slot v-else />
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { safeImageUrl } from '@/utilities/eventPresentation'

defineOptions({ inheritAttrs: false })
const props = withDefaults(defineProps<{ sources: readonly string[]; alt?: string }>(), { alt: '' })
const failed = ref<Set<string>>(new Set())
const source = computed(() => props.sources.find(value => safeImageUrl(value) && !failed.value.has(value)))
watch(() => props.sources, () => { failed.value = new Set() })

function onError(): void
{
  if (source.value)
  {
    failed.value = new Set([...failed.value, source.value])
  }
}
</script>
