<template>
  <v-avatar :size="size" :color="color" aria-hidden="true">
    <v-img v-if="avatarUrl && !imageFailed" :key="avatarUrl" :src="avatarUrl" alt="" @error="imageFailed = true" />
    <span v-else>{{ initial }}</span>
  </v-avatar>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { userAvatarColor } from '@/utilities/userAvatar'

const props = withDefaults(defineProps<{ name: string; avatarUrl?: string | null; size?: number | string }>(), { size: 34 })
const imageFailed = ref(false)
const color = computed(() => userAvatarColor(props.name))
const initial = computed(() => Array.from(props.name.trim())[0] || '·')

watch(() => props.avatarUrl, () =>
{
  imageFailed.value = false
})
</script>
