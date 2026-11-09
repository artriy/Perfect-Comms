<script setup lang="ts" generic="T extends string">
import { nextTick, onMounted, ref, watch } from 'vue'

defineProps<{ options: { value: T; label: string }[]; label: string }>()
const model = defineModel<T>({ required: true })

const root = ref<HTMLElement | null>(null)
const pill = ref({ x: 0, width: 0 })
const placed = ref(false)

function place() {
  const active = root.value?.querySelector<HTMLElement>('[aria-pressed="true"]')
  if (!active) return
  pill.value = { x: active.offsetLeft, width: active.offsetWidth }
}

watch(model, () => nextTick(place))

onMounted(() => {
  place()
  document.fonts.ready.then(place)
  requestAnimationFrame(() => (placed.value = true))
})
</script>

<template>
  <div ref="root" class="segmented" :class="{ ready: placed }" role="group" :aria-label="label">
    <span
      class="pill"
      :class="{ placed }"
      :style="{ transform: `translateX(${pill.x}px)`, width: `${pill.width}px` }"
      aria-hidden="true"
    />
    <button
      v-for="option in options"
      :key="option.value"
      type="button"
      :aria-pressed="model === option.value"
      @click="model = option.value"
    >
      {{ option.label }}
    </button>
  </div>
</template>

<style scoped>
.segmented {
  position: relative;
  display: inline-flex;
  padding: 3px;
  border: 1px solid var(--pc-border);
  border-radius: 9px;
  background: var(--pc-row);
}

.pill {
  position: absolute;
  top: 3px;
  bottom: 3px;
  left: 0;
  border-radius: 6px;
  background: var(--pc-active);
}

.pill.placed {
  transition:
    transform 220ms var(--pc-ease-move),
    width 220ms var(--pc-ease-move);
}

button {
  position: relative;
  padding: 8px 16px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--pc-text-2);
  font: inherit;
  font-size: 15px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
  transition:
    color 140ms ease,
    transform 100ms ease;
}

.segmented:not(.ready) button[aria-pressed='true'] {
  background: var(--pc-active);
}

button[aria-pressed='true'] {
  color: var(--pc-text);
}

button:active {
  transform: scale(0.97);
}

@media (hover: hover) and (pointer: fine) {
  button:hover {
    color: var(--pc-text);
  }
}

@media (max-width: 639px) {
  button {
    padding: 7px 12px;
    font-size: 14px;
  }
}
</style>
