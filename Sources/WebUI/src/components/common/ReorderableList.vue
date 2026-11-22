<template>
  <div ref="list" class="reorderable-list" role="list">
    <div v-for="(item, index) in model" :key="item.id" class="reorderable-row" :data-sortable-id="item.id"
         role="listitem">
      <button class="reorder-handle" type="button" :disabled="disabled" :aria-label="label(item, index)"
              :title="t('transcoding.reorderHint')" @keydown="moveWithKeyboard($event, index)">
        <v-icon icon="mdi-drag" size="20"/>
      </button>
      <div class="reorderable-content">
        <slot :item="item" :index="index"/>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts" generic="T extends { id: string }">
import {onBeforeUnmount, onMounted, ref, watch} from 'vue'
import {useI18n} from 'vue-i18n'
import Sortable from 'sortablejs'

const model = defineModel<T[]>({required: true})
const props = defineProps<{ disabled?: boolean; label: (item: T, index: number) => string }>()
const {t} = useI18n()
const list = ref<HTMLElement>()
let sortable: Sortable | undefined

function move(from: number, to: number): void {
  if (props.disabled || from < 0 || to < 0 || to >= model.value.length || from === to) {
    return
  }
  const items = [...model.value]
  items.splice(to, 0, items.splice(from, 1)[0]!)
  model.value = items
}

function moveWithKeyboard(event: KeyboardEvent, index: number): void {
  const target = event.key === 'ArrowUp' ? index - 1 : event.key === 'ArrowDown' ? index + 1 : event.key === 'Home' ? 0 : event.key === 'End' ? model.value.length - 1 : undefined
  if (target === undefined) {
    return
  }
  event.preventDefault()
  move(index, target)
}

onMounted(() => {
  sortable = new Sortable(list.value!, {
    handle: '.reorder-handle', animation: 150, disabled: props.disabled, ghostClass: 'is-dragging',
    onEnd(event) {
      if (event.oldIndex === undefined || event.newIndex === undefined) {
        return
      }
      const siblings = [...event.from.children].filter(child => child !== event.item)
      event.from.insertBefore(event.item, siblings[event.oldIndex] ?? null)
      move(event.oldIndex, event.newIndex)
    },
  })
})
watch(() => props.disabled, disabled => sortable?.option('disabled', !!disabled))
onBeforeUnmount(() => sortable?.destroy())
</script>

<style scoped>
.reorderable-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.reorderable-row {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 12px;
  border: 1px solid rgba(var(--v-theme-on-surface), .12);
  border-radius: 12px;
}

.reorderable-row.is-dragging {
  border-color: rgb(var(--v-theme-primary));
  background: rgba(var(--v-theme-primary), .06);
}

.reorder-handle {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex: none;
  min-width: 32px;
  height: 40px;
  padding: 0;
  border: 0;
  background: transparent;
  color: inherit;
  cursor: grab;
  touch-action: none;
  border-radius: 6px;
}

.reorder-handle:hover:not(:disabled) {
  color: rgb(var(--v-theme-primary));
}

.reorder-handle:active {
  cursor: grabbing;
}

.reorder-handle:focus-visible {
  outline: 2px solid rgb(var(--v-theme-primary));
}

.reorder-handle:disabled {
  opacity: .35;
  cursor: default;
}

.reorderable-content {
  flex: 1;
  min-width: 0;
}
</style>
