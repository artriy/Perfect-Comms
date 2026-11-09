<script setup lang="ts">
defineProps<{
  title: string | null
  host: string
  region: string | null
  code: string
  when: string | null
  status: { text: string; tone: 'warn' | 'step' | 'join' }
  progress?: number
}>()
</script>

<template>
  <div class="lobby" :class="{ 'is-ready': status.tone === 'join' }">
    <div class="info">
      <div v-if="title" class="title">{{ title }}</div>
      <div v-else class="title" :class="{ 'code-title': code }">{{ code ? `Code ${code}` : 'Your host’s lobby' }}</div>
      <div class="meta">
        <span v-if="host">host {{ host }}</span>
        <span v-if="region">{{ region }}</span>
        <code v-if="code && title">{{ code }}</code>
      </div>
    </div>
    <span v-if="when" class="when">{{ when }}</span>
    <span class="status" :class="status.tone">
      <Transition name="roll">
        <span :key="status.text">{{ status.text }}</span>
      </Transition>
    </span>
    <span v-if="progress !== undefined" class="progress" aria-hidden="true">
      <span :style="{ transform: `scaleX(${progress})` }" />
    </span>
  </div>
</template>

<style scoped>
.lobby {
  position: relative;
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto auto;
  align-items: center;
  gap: 18px;
  padding: 14px 16px 14px 20px;
  border: 1px solid var(--pc-border);
  border-radius: 10px;
  background: var(--pc-row);
}

.lobby::after {
  content: '';
  position: absolute;
  inset: -1px;
  border-radius: inherit;
  pointer-events: none;
}

.lobby.is-ready::after {
  animation: pc-ready 700ms var(--pc-ease-out);
}

@keyframes pc-ready {
  from {
    box-shadow: 0 0 0 0 rgba(106, 203, 236, 0.45);
  }
  to {
    box-shadow: 0 0 0 14px rgba(106, 203, 236, 0);
  }
}

.progress {
  position: absolute;
  left: 12px;
  right: 12px;
  bottom: -1px;
  height: 2px;
  overflow: hidden;
  border-radius: 1px;
}

.progress > span {
  display: block;
  height: 100%;
  background: var(--pc-cyan);
  transform-origin: left;
  transition: transform 420ms var(--pc-ease-out);
}

.title {
  overflow: hidden;
  font-size: 18px;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.meta {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 12px;
  margin-top: 2px;
  font-size: 13.5px;
  color: var(--pc-text-2);
}

.meta code {
  color: var(--pc-text);
  font-family: var(--pc-mono);
  font-size: 13px;
  letter-spacing: 0.08em;
}

.when {
  padding: 4px 10px;
  border-radius: 6px;
  background: var(--pc-raised);
  font-size: 13px;
  color: var(--pc-text-2);
  font-variant-numeric: tabular-nums;
}

.status {
  position: relative;
  display: grid;
  min-width: 150px;
  height: 40px;
  overflow: hidden;
  border: 1px solid var(--pc-warning);
  border-radius: 8px;
  background: rgba(255, 204, 102, 0.08);
  color: var(--pc-warning);
  font-size: 14px;
  font-weight: 600;
  transition:
    background-color 200ms ease,
    border-color 200ms ease,
    color 200ms ease;
}

.status > span {
  grid-area: 1 / 1;
  align-self: center;
  justify-self: center;
  padding: 0 14px;
  white-space: nowrap;
}

.status.step {
  border-color: var(--pc-border);
  background: var(--pc-raised);
  color: var(--pc-text);
}

.status.join {
  border-color: var(--pc-cyan);
  background: var(--pc-cyan);
  color: var(--pc-ink);
}

.roll-enter-active,
.roll-leave-active {
  transition:
    transform 240ms var(--pc-ease-out),
    opacity 240ms var(--pc-ease-out);
}

.roll-enter-from {
  opacity: 0;
  transform: translateY(100%);
}

.roll-leave-to {
  opacity: 0;
  transform: translateY(-100%);
}

@media (max-width: 639px) {
  .lobby {
    grid-template-columns: minmax(0, 1fr) auto;
    gap: 8px 12px;
    padding: 12px 12px 12px 14px;
  }

  .title {
    font-size: 16px;
  }

  .when {
    display: none;
  }

  .status {
    min-width: 118px;
    height: 36px;
    font-size: 13px;
  }
}

.code-title {
  font-family: var(--pc-mono);
  font-size: 17px;
  letter-spacing: 0.04em;
}
</style>
