<template>
  <v-dialog v-model="opened" max-width="520" scrollable :persistent="busy" :aria-labelledby="titleId" @after-leave="reset">
    <ContentCard :title="t('shell.account.avatar.crop')" :title-id="titleId">
      <form :id="formId" class="form-stack" @submit.prevent="save">
        <v-file-input :model-value="file" :label="t('shell.account.avatar.select')" :accept="acceptedTypes"
                      :loading="decoding" :disabled="busy" @update:model-value="select"/>
        <template v-if="image">
          <canvas ref="preview" class="avatar-crop-preview rounded-lg" :width="SIZE" :height="SIZE" role="img"
                  :aria-label="t('shell.account.avatar.preview')" @pointerdown="beginDrag" @pointermove="drag"
                  @pointerup="endDrag" @pointercancel="endDrag" @lostpointercapture="endDrag"/>
          <v-slider v-model="zoom" :min="1" :max="4" :step="0.01" :label="t('shell.account.avatar.zoom')"
                    hide-details :disabled="busy"/>
          <div class="form-row">
            <v-slider v-model="horizontal" :min="0" :max="100" :step="1" :label="t('shell.account.avatar.horizontal')"
                      hide-details :disabled="busy || crop?.travelX === 0"/>
            <v-slider v-model="vertical" :min="0" :max="100" :step="1" :label="t('shell.account.avatar.vertical')"
                      hide-details :disabled="busy || crop?.travelY === 0"/>
          </div>
        </template>
        <v-alert v-if="selectionError || error" type="error">{{ selectionError || error }}</v-alert>
      </form>
      <template #actions>
        <v-btn variant="text" :disabled="busy" @click="opened = false">{{ t('views.common.cancel') }}</v-btn>
        <v-btn type="submit" :form="formId" color="primary" :loading="busy" :disabled="!image || decoding">
          {{ t('views.common.save') }}
        </v-btn>
      </template>
    </ContentCard>
  </v-dialog>
</template>

<script setup lang="ts">
import {computed, onBeforeUnmount, ref, shallowRef, useId, watchEffect} from 'vue'
import {useI18n} from 'vue-i18n'
import ContentCard from '@/components/common/ContentCard.vue'
import {useAction} from '@/composables/useAction'
import {useAuthStore} from '@/stores/auth'

const SIZE = 256
const MAX_FILE_BYTES = 10 * 1024 * 1024
const MAX_UPLOAD_BYTES = 128 * 1024
const allowedTypes = ['image/jpeg', 'image/png', 'image/webp']
const acceptedTypes = allowedTypes.join(',')
const {t} = useI18n()
const auth = useAuthStore()
const opened = defineModel<boolean>({required: true})
const emit = defineEmits<{ saved: [] }>()
const titleId = useId()
const formId = useId()
const file = shallowRef<File | null>(null)
const image = shallowRef<ImageBitmap>()
const preview = ref<HTMLCanvasElement>()
const zoom = ref(1)
const horizontal = ref(50)
const vertical = ref(50)
const decoding = ref(false)
const selectionError = ref('')
const {busy, error, run} = useAction()
let selection = 0
let pointer: { id: number; x: number; y: number; horizontal: number; vertical: number } | undefined

const crop = computed(() => {
  if (!image.value) {
    return
  }
  const side = Math.min(image.value.width, image.value.height) / zoom.value
  return {side, travelX: image.value.width - side, travelY: image.value.height - side}
})

watchEffect(() => {
  const drawing = preview.value?.getContext('2d')
  if (drawing && image.value && crop.value) {
    const {side, travelX, travelY} = crop.value
    drawing.fillStyle = '#fff'
    drawing.fillRect(0, 0, SIZE, SIZE)
    drawing.drawImage(image.value, travelX * horizontal.value / 100, travelY * vertical.value / 100, side, side, 0, 0, SIZE, SIZE)
  }
})

function reset(): void {
  selection++
  file.value = null
  image.value?.close()
  image.value = undefined
  pointer = undefined
  decoding.value = false
  selectionError.value = ''
  error.value = ''
  zoom.value = 1
  horizontal.value = 50
  vertical.value = 50
}

async function select(value: File | File[] | null): Promise<void> {
  reset()
  const selected = Array.isArray(value) ? value[0] : value
  if (!selected) {
    return
  }
  file.value = selected
  if (!allowedTypes.includes(selected.type) || selected.size > MAX_FILE_BYTES) {
    selectionError.value = t('shell.account.avatar.fileLimit')
    return
  }
  const operation = selection
  decoding.value = true
  try {
    const bitmap = await createImageBitmap(selected)
    if (operation !== selection) {
      bitmap.close()
      return
    }
    if (bitmap.width * bitmap.height > 40_000_000) {
      bitmap.close()
      throw new Error()
    }
    image.value = bitmap
  } catch {
    if (operation === selection) {
      selectionError.value = t('shell.account.avatar.invalid')
    }
  } finally {
    if (operation === selection) {
      decoding.value = false
    }
  }
}

function beginDrag(event: PointerEvent): void {
  if (!busy.value && event.button === 0 && preview.value) {
    preview.value.setPointerCapture(event.pointerId)
    pointer = {id: event.pointerId, x: event.clientX, y: event.clientY, horizontal: horizontal.value, vertical: vertical.value}
  }
}

function drag(event: PointerEvent): void {
  if (!pointer || pointer.id !== event.pointerId || !crop.value || !preview.value) {
    return
  }
  const width = preview.value.getBoundingClientRect().width
  const {side, travelX, travelY} = crop.value
  if (travelX > 0) {
    horizontal.value = Math.max(0, Math.min(100, pointer.horizontal - (event.clientX - pointer.x) / width * side / travelX * 100))
  }
  if (travelY > 0) {
    vertical.value = Math.max(0, Math.min(100, pointer.vertical - (event.clientY - pointer.y) / width * side / travelY * 100))
  }
}

function endDrag(): void {
  pointer = undefined
}

async function save(): Promise<void> {
  const canvas = preview.value
  if (busy.value || !canvas || !image.value) {
    return
  }
  await run(async () => {
    let blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/jpeg', 0.88))
    if (blob && blob.size > MAX_UPLOAD_BYTES) {
      blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/jpeg', 0.64))
    }
    if (!blob || blob.size > MAX_UPLOAD_BYTES) {
      throw new Error(t('shell.account.avatar.invalid'))
    }
    await auth.changeAvatar(blob)
    opened.value = false
    emit('saved')
  })
}

onBeforeUnmount(reset)
</script>

<style scoped>
.avatar-crop-preview {
  display: block;
  width: min(100%, 256px);
  height: auto;
  margin-inline: auto;
  cursor: grab;
  touch-action: none;
}

.avatar-crop-preview:active {
  cursor: grabbing;
}
</style>
