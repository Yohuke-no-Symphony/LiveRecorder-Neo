<template>
  <v-card class="content-card" :class="{ 'content-card-headed': title }">
    <CardHeader v-if="title" :title="title" :title-id="titleId" class="content-card-header">
      <template v-if="$slots.suffix" #suffix>
        <slot name="suffix"/>
      </template>
      <template v-if="$slots['header-actions']" #actions>
        <slot name="header-actions"/>
      </template>
    </CardHeader>
    <v-card-text class="content-card-body">
      <slot/>
    </v-card-text>
    <CardActions v-if="$slots.actions">
      <slot name="actions"/>
    </CardActions>
  </v-card>
</template>

<script setup lang="ts">
import CardActions from './CardActions.vue'
import CardHeader from './CardHeader.vue'

defineProps<{ title?: string; titleId?: string }>()
</script>

<style scoped>
.content-card {
  min-width: 0;
}

.content-card > .content-card-header {
  flex-shrink: 0;
}

.content-card > .content-card-body {
  min-width: 0;
  min-height: 0;
  padding: var(--ui-card-padding);
  overflow-y: auto;
}

.content-card-headed > .content-card-body {
  padding-top: var(--ui-card-header-padding);
}
</style>
