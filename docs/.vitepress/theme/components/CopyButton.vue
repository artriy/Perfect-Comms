<script setup lang="ts">
import { onUnmounted, ref } from 'vue'

const props = defineProps<{ text: string; label: string; primary?: boolean }>()
const state = ref<'idle' | 'copied' | 'failed'>('idle')
let timer: ReturnType<typeof setTimeout> | undefined

async function copy() {
  clearTimeout(timer)
  try {
    await navigator.clipboard.writeText(props.text)
    state.value = 'copied'
  } catch {
    state.value = 'failed'
  }
  timer = setTimeout(() => (state.value = 'idle'), 1400)
}

onUnmounted(() => clearTimeout(timer))
</script>

<template>
  <button type="button" class="copy" :class="{ primary }" @click="copy">
    <Transition name="swap" mode="out-in">
      <span v-if="state === 'copied'" key="copied" class="face">
        <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5 6.2 11.5 13 4.5" /></svg>
        Copied
      </span>
      <span v-else-if="state === 'failed'" key="failed" class="face">Copy blocked, select and copy</span>
      <span v-else key="idle" class="face">{{ label }}</span>
    </Transition>
  </button>
</template>

<style scoped>
.copy {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  height: 40px;
  padding: 0 18px;
  border: 0;
  border-radius: 8px;
  background: var(--pc-raised);
  color: var(--pc-text);
  font: inherit;
  font-size: 15px;
  font-weight: 600;
  cursor: pointer;
  transition:
    background-color 140ms ease,
    transform 100ms ease;
}

.copy.primary {
  height: 46px;
  padding: 0 24px;
  background: var(--pc-cyan);
  color: var(--pc-ink);
  font-size: 16px;
}

.copy:active {
  transform: scale(0.97);
}

.face {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.face svg {
  width: 16px;
  height: 16px;
  fill: none;
  stroke: currentColor;
  stroke-width: 2.4;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.swap-enter-active {
  transition:
    opacity 160ms var(--pc-ease-out),
    transform 160ms var(--pc-ease-out);
}

.swap-leave-active {
  transition:
    opacity 90ms ease,
    transform 90ms ease;
}

.swap-enter-from {
  opacity: 0;
  transform: translateY(6px);
}

.swap-leave-to {
  opacity: 0;
  transform: translateY(-6px);
}

@media (hover: hover) and (pointer: fine) {
  .copy:hover {
    background: var(--pc-active);
  }

  .copy.primary:hover {
    background: var(--pc-cyan-hover);
  }
}
</style>
