<script setup lang="ts">
defineProps<{ label: string }>()
const model = defineModel<boolean>({ required: true })
</script>

<template>
  <button type="button" class="toggle" role="switch" :aria-checked="model" :aria-label="label" @click="model = !model" />
</template>

<style scoped>
.toggle {
  position: relative;
  flex: none;
  width: 62px;
  height: 32px;
  padding: 0;
  border: 0;
  border-radius: 16px;
  background: var(--pc-track);
  cursor: pointer;
  transition: background-color 160ms ease;
}

.toggle::after {
  content: '';
  position: absolute;
  top: 4px;
  left: 4px;
  width: 24px;
  height: 24px;
  border-radius: 50%;
  background: #c4c8d8;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.35);
  transition:
    transform 180ms var(--pc-ease-out),
    background-color 160ms ease;
}

.toggle[aria-checked='true'] {
  background: var(--pc-cyan);
}

.toggle[aria-checked='true']::after {
  background: #f2f4fa;
  transform: translateX(30px);
}

.toggle:active::after {
  width: 28px;
}

.toggle[aria-checked='true']:active::after {
  transform: translateX(26px);
}
</style>
