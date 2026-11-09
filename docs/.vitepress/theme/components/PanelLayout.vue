<script setup lang="ts">
import { computed, defineAsyncComponent, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { Content, useData, withBase } from 'vitepress'
import { data as releases } from '../releases.data'
import { REPO } from '../rail'
import PanelRail from './PanelRail.vue'
import PageOutline from './PageOutline.vue'
import PageFoot from './PageFoot.vue'
import NotFound from './NotFound.vue'

const VPLocalSearchBox = defineAsyncComponent(() => import('vitepress/dist/client/theme-default/components/VPLocalSearchBox.vue'))

const { page } = useData()
const latest = releases[0]
const isHome = computed(() => page.value.relativePath === 'index.md')
const searchOpen = ref(false)
const navigated = ref(false)
let returnFocus: HTMLElement | null = null

function openSearch() {
  returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
  searchOpen.value = true
  requestAnimationFrame(labelSearch)
}

function labelSearch() {
  if (!searchOpen.value) return
  const shell = document.querySelector('.VPLocalSearchBox .shell')
  if (!shell) return requestAnimationFrame(labelSearch)
  shell.setAttribute('role', 'dialog')
  shell.setAttribute('aria-modal', 'true')
  shell.setAttribute('aria-label', 'Search docs')
}

function closeSearch() {
  searchOpen.value = false
  nextTick(() => returnFocus?.focus())
}

function skipToContent() {
  document.getElementById('main')?.focus()
}

function editing(target: EventTarget | null) {
  const el = target as HTMLElement | null
  return !!el && (el.isContentEditable || ['INPUT', 'SELECT', 'TEXTAREA'].includes(el.tagName))
}

function onKey(event: KeyboardEvent) {
  if ((event.key.toLowerCase() === 'k' && (event.ctrlKey || event.metaKey)) || (event.key === '/' && !editing(event.target))) {
    event.preventDefault()
    openSearch()
  }
}

function arrive() {
  const id = decodeURIComponent(location.hash.slice(1))
  const target = id && document.getElementById(id)
  if (!target) return
  target.classList.remove('pc-arrived')
  void target.offsetWidth
  target.classList.add('pc-arrived')
  target.addEventListener('animationend', () => target.classList.remove('pc-arrived'), { once: true })
}

watch(
  () => page.value.relativePath,
  () => {
    navigated.value = true
    nextTick(() => requestAnimationFrame(arrive))
  },
)

onMounted(() => {
  addEventListener('keydown', onKey)
  addEventListener('hashchange', arrive)
  requestAnimationFrame(arrive)
})

onUnmounted(() => {
  removeEventListener('keydown', onKey)
  removeEventListener('hashchange', arrive)
})
</script>

<template>
  <div class="stage">
    <a class="skip" href="#main" @click.prevent="skipToContent">Skip to content</a>
    <div class="scroll-mark" aria-hidden="true" />
    <div class="panel">
      <header class="head">
        <a class="brand" :href="withBase('/')">
          <img :src="withBase('/miclogo.png')" alt="" width="40" height="40" />
          <span class="name">Perfect Comms</span>
          <span class="tag">Proximity voice chat for Among Us</span>
        </a>
        <button type="button" class="search" aria-label="Search docs" aria-keyshortcuts="Control+K" @click="openSearch">
          <svg viewBox="0 0 16 16" aria-hidden="true"><circle cx="7" cy="7" r="4.5" /><path d="M10.5 10.5 14 14" /></svg>
          <span class="placeholder">Search docs</span>
          <kbd>Ctrl K</kbd>
        </button>
        <a v-if="!isHome" class="get" :href="latest.desktop!">
          <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 3v10M5.5 8.5 10 13l4.5-4.5M4 16.5h12" /></svg>
          Download {{ latest.tag }}
        </a>
        <a class="github" :href="REPO" aria-label="Perfect Comms on GitHub">
          <svg viewBox="0 0 16 16" aria-hidden="true">
            <path
              d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z"
            />
          </svg>
        </a>
      </header>
      <div class="body" :class="{ wide: !isHome && !page.isNotFound }">
        <PanelRail />
        <main id="main" class="main" tabindex="-1">
          <NotFound v-if="page.isNotFound" @search="openSearch" />
          <div v-else :key="page.relativePath" class="page" :class="{ enter: navigated }">
            <Content v-if="isHome" />
            <template v-else>
              <Content class="vp-doc pc-doc" />
              <PageFoot />
            </template>
          </div>
        </main>
        <PageOutline v-if="!isHome && !page.isNotFound" class="aside" />
      </div>
      <footer class="foot">
        <span>Open source on <a :href="REPO">GitHub</a></span>
        <span>Not affiliated with Innersloth or Among Us</span>
      </footer>
    </div>
    <VPLocalSearchBox v-if="searchOpen" @close="closeSearch" />
  </div>
</template>

<style scoped>
.stage {
  --pc-head-h: 76px;
  min-height: 100vh;
  padding: 20px 20px 24px;
}

.scroll-mark {
  position: fixed;
  top: 0;
  left: 0;
  width: 1px;
  height: var(--pc-head-h);
  visibility: hidden;
  pointer-events: none;
}

.skip {
  position: fixed;
  top: 12px;
  left: 12px;
  z-index: 100;
  padding: 10px 16px;
  border-radius: 8px;
  background: var(--pc-cyan);
  color: var(--pc-ink);
  font-weight: 600;
  text-decoration: none;
  transform: translateY(-200%);
}

.skip:focus {
  transform: none;
}

.panel {
  position: relative;
  max-width: 1440px;
  min-height: calc(100vh - 44px);
  margin: 0 auto;
  display: grid;
  grid-template-rows: auto 1fr auto;
  border-radius: 16px;
  background: var(--pc-bg);
  box-shadow:
    0 0 0 1px #1d2030,
    0 30px 90px rgba(0, 0, 0, 0.55);
}

.head {
  position: sticky;
  top: 0;
  z-index: 20;
  display: flex;
  align-items: center;
  gap: 14px;
  height: var(--pc-head-h);
  padding: 0 24px 0 28px;
  border-bottom: 1px solid var(--pc-border);
  border-radius: 16px 16px 0 0;
  background: var(--pc-bg);
}

.head::before {
  content: '';
  position: absolute;
  top: 0;
  left: 24px;
  right: 24px;
  height: 2px;
  border-radius: 2px;
  background: linear-gradient(90deg, var(--pc-cyan), var(--pc-violet));
  opacity: 0.85;
}

.brand {
  display: grid;
  grid-template-columns: 40px auto;
  grid-template-rows: auto auto;
  column-gap: 14px;
  align-items: center;
  margin-right: auto;
  color: var(--pc-text);
  text-decoration: none;
}

.brand img {
  grid-row: 1 / 3;
  width: 40px;
  height: 40px;
}

.name {
  font-size: 24px;
  font-weight: 600;
  line-height: 1.1;
  letter-spacing: -0.01em;
}

.tag {
  font-size: 13.5px;
  color: var(--pc-text-2);
}

.search {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 300px;
  height: 40px;
  padding: 0 8px 0 12px;
  border: 1px solid var(--pc-border);
  border-radius: 8px;
  background: var(--pc-row);
  color: var(--pc-text-3);
  font: inherit;
  font-size: 14px;
  cursor: pointer;
  transition: border-color 140ms ease;
}

.search svg {
  width: 16px;
  height: 16px;
  flex: none;
  fill: none;
  stroke: currentColor;
  stroke-width: 2;
}

.placeholder {
  flex: 1;
  text-align: left;
}

.search kbd {
  height: 22px;
  font-size: 11px;
}

.get {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  height: 40px;
  padding: 0 16px;
  border-radius: 8px;
  background: var(--pc-cyan);
  color: var(--pc-ink);
  font-size: 15px;
  font-weight: 600;
  text-decoration: none;
  white-space: nowrap;
  transition:
    background-color 140ms ease,
    transform 100ms ease;
}

.get svg {
  width: 16px;
  height: 16px;
  fill: none;
  stroke: currentColor;
  stroke-width: 2.2;
}

.github {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 40px;
  height: 40px;
  border-radius: 8px;
  color: var(--pc-text-2);
  transition:
    background-color 140ms ease,
    color 140ms ease;
}

.github svg {
  width: 20px;
  height: 20px;
  fill: currentColor;
}

.get:active,
.search:active,
.github:active {
  transform: scale(0.97);
}

.body {
  display: grid;
  grid-template-columns: 248px minmax(0, 1fr);
  align-items: stretch;
  background: linear-gradient(90deg, var(--pc-rail) 247px, var(--pc-border) 247px 248px, transparent 248px);
}

.main {
  min-width: 0;
  padding: 40px 48px 64px;
  outline: none;
}

.page {
  max-width: 880px;
}

.wide .page {
  max-width: 800px;
}

.page.enter {
  animation: pc-enter 200ms var(--pc-ease-out) both;
}

.aside {
  display: none;
}

.foot {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 8px 24px;
  padding: 16px 28px;
  border-top: 1px solid var(--pc-border);
  font-size: 13px;
  color: var(--pc-text-3);
}

.foot a {
  color: var(--pc-text-2);
  text-decoration: none;
}

@media (hover: hover) and (pointer: fine) {
  .search:hover {
    border-color: var(--pc-text-3);
  }

  .get:hover {
    background: #8ad8f2;
  }

  .github:hover {
    background: var(--pc-raised);
    color: var(--pc-text);
  }

  .foot a:hover {
    color: var(--pc-text);
  }
}

@media (min-width: 1280px) {
  .body.wide {
    grid-template-columns: 248px minmax(0, 1fr) 248px;
  }

  .wide .aside {
    display: block;
  }
}

@media (max-width: 1099px) {
  .stage {
    padding: 0;
  }

  .panel {
    min-height: 100vh;
    border-radius: 0;
    box-shadow: none;
  }

  .head {
    border-radius: 0;
  }

  .head::before {
    left: 0;
    right: 0;
  }

  .search {
    width: 240px;
  }
}

@media (max-width: 899px) {
  .stage {
    --pc-head-h: 60px;
  }

  .scroll-mark {
    height: calc(var(--pc-head-h) + 49px);
  }

  .head {
    gap: 8px;
    padding: 0 12px 0 14px;
  }

  .brand {
    grid-template-columns: 30px auto;
    column-gap: 10px;
  }

  .brand img {
    width: 30px;
    height: 30px;
  }

  .name {
    grid-row: 1 / 3;
    font-size: 19px;
  }

  .tag,
  .get,
  .placeholder,
  .search kbd {
    display: none;
  }

  .search {
    justify-content: center;
    width: 40px;
    padding: 0;
  }

  .body {
    grid-template-columns: minmax(0, 1fr);
    background: none;
  }

  .main {
    padding: 24px 16px 40px;
  }

  .foot {
    padding: 14px 16px;
    font-size: 12px;
  }
}
</style>
