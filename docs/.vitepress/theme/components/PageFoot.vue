<script setup lang="ts">
import { computed } from 'vue'
import { useData, withBase } from 'vitepress'
import { RAIL_ORDER, REPO, pageOf } from '../rail'

const { page } = useData()

const index = computed(() => RAIL_ORDER.findIndex((item) => pageOf(item.link) === page.value.relativePath))
const previous = computed(() => (index.value > 0 ? RAIL_ORDER[index.value - 1] : null))
const next = computed(() => (index.value >= 0 && index.value < RAIL_ORDER.length - 1 ? RAIL_ORDER[index.value + 1] : null))
const updated = computed(() =>
  page.value.lastUpdated ? new Date(page.value.lastUpdated).toLocaleDateString('en', { dateStyle: 'medium' }) : null,
)
</script>

<template>
  <footer class="page-foot">
    <nav v-if="previous || next" class="pager" aria-label="Previous and next page">
      <a v-if="previous" class="step prev" :href="withBase(previous.link)">
        <span class="dir">Previous</span>
        <span class="name">{{ previous.text }}</span>
      </a>
      <a v-if="next" class="step next" :href="withBase(next.link)">
        <span class="dir">Next</span>
        <span class="name">{{ next.text }}</span>
      </a>
    </nav>
    <p class="meta">
      <a :href="`${REPO}/edit/main/docs/${page.relativePath}`">Edit this page on GitHub</a>
      <span v-if="updated">Updated {{ updated }}</span>
    </p>
  </footer>
</template>

<style scoped>
.page-foot {
  margin-top: 56px;
}

.pager {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
}

.step {
  display: grid;
  gap: 2px;
  padding: 14px 18px;
  border: 1px solid var(--pc-border);
  border-radius: 10px;
  background: var(--pc-row);
  text-decoration: none;
  transition:
    border-color 140ms ease,
    background-color 140ms ease,
    transform 100ms ease;
}

.step:active {
  transform: scale(0.98);
}

.next {
  grid-column: 2;
  text-align: right;
}

.dir {
  font-size: 13px;
  color: var(--pc-text-3);
}

.name {
  font-size: 17px;
  font-weight: 500;
  color: var(--pc-text);
}

.prev .name::before {
  content: '\2039\00a0';
  color: var(--pc-cyan);
}

.next .name::after {
  content: '\00a0\203a';
  color: var(--pc-cyan);
}

.meta {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 8px 24px;
  margin: 20px 2px 0;
  font-size: 13px;
  color: var(--pc-text-3);
}

.meta a {
  color: var(--pc-text-2);
  text-decoration: none;
}

@media (hover: hover) and (pointer: fine) {
  .step:hover {
    border-color: var(--pc-text-3);
    background: var(--pc-raised);
  }

  .meta a:hover {
    color: var(--pc-text);
  }
}

@media (max-width: 639px) {
  .pager {
    grid-template-columns: 1fr;
  }

  .next {
    grid-column: 1;
  }
}
</style>
