<script setup lang="ts">
defineProps<{ title: string; hint: string }>()
</script>

<template>
  <figure class="hud-status">
    <div class="screen">
      <span class="title">{{ title }}</span>
      <span class="hint">{{ hint }}</span>
    </div>
    <figcaption>The voice HUD message. In the main menu and lobbies the same problem opens a card with Retry and Open guide.</figcaption>
  </figure>
</template>

<style scoped>
.hud-status {
  margin: 16px 0;
}

.screen {
  display: grid;
  justify-items: start;
  gap: 2px;
  padding: 18px 20px 40px;
  border-radius: 12px;
  background:
    radial-gradient(ellipse at 72% 125%, transparent 0 30%, rgba(8, 10, 16, 0.9) 72%),
    repeating-linear-gradient(0deg, transparent 0 27px, #343a54 27px 28px),
    repeating-linear-gradient(90deg, transparent 0 27px, #343a54 27px 28px),
    #262b40;
}

.title {
  font-size: 17px;
  font-weight: 600;
  color: var(--pc-danger);
  text-shadow: 0 1px 2px rgba(0, 0, 0, 0.9);
}

.hint {
  font-size: 14px;
  color: var(--pc-text);
  text-shadow: 0 1px 2px rgba(0, 0, 0, 0.9);
}

figcaption {
  margin-top: 8px;
  font-size: 13px;
  color: var(--pc-text-3);
}
</style>
