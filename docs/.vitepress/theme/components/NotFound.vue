<script setup lang="ts">
import { withBase } from 'vitepress'

defineEmits<{ search: [] }>()

const destinations = [
  { text: 'Install', hint: 'PerfectComms.dll into BepInEx/plugins', link: '/install' },
  { text: 'Fix problems', hint: 'No voice, antivirus, OBS', link: '/troubleshooting' },
  { text: 'Host settings', hint: 'Distance, walls, ghosts, Team Radio', link: '/host' },
  { text: 'Mod API', hint: 'Add voice rules to your mod', link: '/api/' },
]
</script>

<template>
  <section class="lost">
    <p class="code">404</p>
    <h1>This page did not come through.</h1>
    <p class="why">The link may be old, or the page moved. Search, or pick a tab.</p>
    <div class="actions">
      <button type="button" class="search" @click="$emit('search')">Search docs</button>
      <a class="home" :href="withBase('/')">Back to Tonight</a>
    </div>
    <div class="rows">
      <a v-for="item in destinations" :key="item.link" class="row" :href="withBase(item.link)">
        <span class="name">{{ item.text }}</span>
        <span class="hint">{{ item.hint }}</span>
      </a>
    </div>
  </section>
</template>

<style scoped>
.lost {
  max-width: 720px;
  padding: 24px 0 40px;
}

.code {
  margin: 0;
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.14em;
  color: var(--pc-warning);
}

h1 {
  margin: 8px 0 0;
  font-size: clamp(34px, 5vw, 52px);
  font-weight: 600;
  line-height: 1.04;
  letter-spacing: -0.025em;
}

.why {
  margin: 14px 0 0;
  font-size: 17px;
  color: var(--pc-text-2);
}

.actions {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  margin-top: 28px;
}

.actions > * {
  display: inline-flex;
  align-items: center;
  height: 46px;
  padding: 0 22px;
  border: 0;
  border-radius: 9px;
  font: inherit;
  font-size: 16px;
  font-weight: 600;
  text-decoration: none;
  cursor: pointer;
  transition:
    background-color 140ms ease,
    transform 100ms ease;
}

.actions > *:active {
  transform: scale(0.97);
}

.search {
  background: var(--pc-cyan);
  color: var(--pc-ink);
}

.home {
  background: var(--pc-raised);
  color: var(--pc-text);
}

.rows {
  margin-top: 40px;
  padding: 0 20px;
  border: 1px solid var(--pc-border);
  border-radius: 10px;
  background: var(--pc-row);
}

.row {
  display: grid;
  grid-template-columns: minmax(140px, 200px) 1fr;
  align-items: baseline;
  gap: 16px;
  padding: 14px 0;
  border-top: 1px solid var(--pc-border);
  text-decoration: none;
}

.row:first-child {
  border-top: 0;
}

.name {
  font-size: 17px;
  font-weight: 500;
  color: var(--pc-text);
}

.hint {
  font-size: 14px;
  color: var(--pc-text-2);
}

@media (hover: hover) and (pointer: fine) {
  .search:hover {
    background: #8ad8f2;
  }

  .home:hover {
    background: var(--pc-active);
  }

  .row:hover .name {
    color: var(--pc-cyan);
  }
}

@media (max-width: 639px) {
  .row {
    grid-template-columns: 1fr;
    gap: 2px;
  }
}
</style>
