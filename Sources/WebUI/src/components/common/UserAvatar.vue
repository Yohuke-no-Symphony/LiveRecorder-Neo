<template>
  <v-avatar :size="size" :color="color" aria-hidden="true">
    <v-img v-if="imageUrl && !imageFailed" :key="imageUrl" :src="imageUrl" alt="" @error="imageFailed = true" />
    <span v-else>{{ initial }}</span>
  </v-avatar>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { getServerUrl } from '@/apis/ServerURL'
import { userAvatarColor } from '@/utilities/userAvatar'

const props = withDefaults(defineProps<{ name: string; avatarUrl?: string | null; size?: number | string }>(), { size: 34 })
const imageFailed = ref(false)
const imageUrl = computed(() => props.avatarUrl?.startsWith('/api/auth/avatars/') ? getServerUrl(props.avatarUrl) : props.avatarUrl)
const color = computed(() => userAvatarColor(props.name))
const initial = computed(() => Array.from(props.name.trim())[0] || '·')

watch(imageUrl, () =>
{
  imageFailed.value = false
})
</script>
