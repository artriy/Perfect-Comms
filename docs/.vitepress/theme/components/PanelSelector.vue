<script setup lang="ts" generic="T extends string">
import { computed, ref } from 'vue'

const props = defineProps<{ options: { value: T; label: string }[]; label: string }>()
const model = defineModel<T>({ required: true })

const index = computed(() => props.options.findIndex((option) => option.value === model.value))
const current = computed(() => props.options[index.value])
const direction = ref(1)

function step(delta: number) {
  const next = props.options[index.value + delta]
  if (!next) return
  direction.value = delta
  model.value = next.value
}
</script>

<template>
  <div class="selector" role="group" :aria-label="label">
    <button type="button" class="arrow" :aria-label="`Previous ${label}`" :disabled="index <= 0" @click="step(-1)">
      <svg viewBox="0 0 14 14" aria-hidden="true"><path d="M9 3 5 7l4 4" /></svg>
    </button>
    <output class="value" aria-live="polite">
      <Transition :name="direction < 0 ? 'back' : 'forward'">
        <span :key="current?.value">{{ current?.label }}</span>
      </Transition>
    </output>
    <button type="button" class="arrow" :aria-label="`Next ${label}`" :disabled="index >= options.length - 1" @click="step(1)">
      <svg viewBox="0 0 14 14" aria-hidden="true"><path d="m5 3 4 4-4 4" /></svg>
    </button>
  </div>
</template>

<style scoped>
.selector {
  display: flex;
  align-items: center;
  gap: 6px;
  width: min(100%, 300px);
}

.arrow {
  display: flex;
  align-items: center;
  justify-content: center;
  flex: none;
  width: 38px;
  height: 38px;
  border: 0;
  border-radius: 8px;
  background: var(--pc-raised);
  cursor: pointer;
  transition:
    background-color 140ms ease,
    transform 100ms ease;
}

.arrow svg {
  width: 14px;
  height: 14px;
  fill: none;
  stroke: var(--pc-text-2);
  stroke-width: 2;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.arrow:active:not(:disabled) {
  transform: scale(0.94);
}

.arrow:disabled {
  cursor: default;
  opacity: 0.4;
}

.value {
  position: relative;
  display: grid;
  flex: 1;
  min-width: 0;
  height: 38px;
  overflow: hidden;
  border-radius: 8px;
  background: var(--pc-raised);
  color: var(--pc-text);
  font-size: 15px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}

.value > span {
  grid-area: 1 / 1;
  align-self: center;
  justify-self: center;
  padding: 0 12px;
  white-space: nowrap;
}

.forward-enter-active,
.back-enter-active {
  transition:
    transform 180ms var(--pc-ease-out),
    opacity 180ms var(--pc-ease-out);
}

.forward-leave-active,
.back-leave-active {
  transition:
    transform 120ms ease,
    opacity 120ms ease;
}

.forward-enter-from,
.back-leave-to {
  opacity: 0;
  transform: translateX(14px);
}

.forward-leave-to,
.back-enter-from {
  opacity: 0;
  transform: translateX(-14px);
}

@media (hover: hover) and (pointer: fine) {
  .arrow:hover:not(:disabled) {
    background: var(--pc-active);
  }
}
</style>
