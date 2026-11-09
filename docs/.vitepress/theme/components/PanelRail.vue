<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useData, withBase } from 'vitepress'
import { RAIL, pageOf, type RailLink } from '../rail'

const { page } = useData()
const rail = ref<HTMLElement | null>(null)
const bar = ref<HTMLElement | null>(null)
const placed = ref(false)
const pending = ref<string | null>(null)

const current = computed(() => page.value.relativePath)
const shown = computed(() => pending.value ?? current.value)
const isCurrent = (item: RailLink) => pageOf(item.link) === current.value
const isActive = (item: RailLink) => pageOf(item.link) === shown.value
const inside = (item: RailLink) => isActive(item) || (item.children ?? []).some(isActive)

function select(event: MouseEvent, item: RailLink) {
  if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return
  pending.value = pageOf(item.link)
  nextTick(() => place(true))
}

let narrow: MediaQueryList | null = null

function place(smooth: boolean) {
  const active = rail.value?.querySelector<HTMLElement>('.tab.active')
  if (!bar.value || !rail.value) return
  if (!active) {
    bar.value.style.opacity = '0'
    return
  }
  bar.value.style.opacity = '1'
  const behavior = smooth ? 'smooth' : 'instant'
  if (narrow?.matches) {
    bar.value.style.height = ''
    bar.value.style.width = `${active.offsetWidth - 16}px`
    bar.value.style.transform = `translateX(${active.offsetLeft + 8}px)`
    rail.value.scrollTo({ left: active.offsetLeft - 48, behavior })
    return
  }
  bar.value.style.width = ''
  bar.value.style.height = `${active.offsetHeight}px`
  bar.value.style.transform = `translateY(${active.offsetTop}px)`
  const { scrollTop, clientHeight } = rail.value
  if (active.offsetTop < scrollTop || active.offsetTop + active.offsetHeight > scrollTop + clientHeight) {
    rail.value.scrollTo({ top: active.offsetTop - clientHeight / 2, behavior })
  }
}

const replace = () => place(false)

watch(current, () => {
  pending.value = null
  nextTick(() => place(true))
})

onMounted(() => {
  narrow = matchMedia('(max-width: 899px)')
  narrow.addEventListener('change', replace)
  addEventListener('resize', replace)
  document.fonts.ready.then(() => {
    place(false)
    requestAnimationFrame(() => (placed.value = true))
  })
})

onUnmounted(() => {
  narrow?.removeEventListener('change', replace)
  removeEventListener('resize', replace)
})
</script>

<template>
  <nav ref="rail" class="rail" :class="{ ready: placed }" aria-label="Pages">
    <div ref="bar" class="bar" aria-hidden="true" />
    <template v-for="group in RAIL" :key="group.label">
      <div class="group">
        {{ group.label }}<kbd v-if="group.key">{{ group.key }}</kbd>
      </div>
      <template v-for="item in group.items" :key="item.link">
        <a
          class="tab"
          :class="{ active: isActive(item) }"
          :href="withBase(item.link)"
          :aria-current="isCurrent(item) ? 'page' : undefined"
          @click="select($event, item)"
          >{{ item.text }}</a
        >
        <div v-if="item.children" class="children" :class="{ open: inside(item) }" :inert="!inside(item)">
          <div>
            <a
              v-for="child in item.children"
              :key="child.link"
              class="tab child"
              :class="{ active: isActive(child) }"
              :href="withBase(child.link)"
              :aria-current="isCurrent(child) ? 'page' : undefined"
              @click="select($event, child)"
              >{{ child.text }}</a
            >
          </div>
        </div>
      </template>
    </template>
  </nav>
</template>

<style scoped>
.rail {
  position: sticky;
  top: var(--pc-head-h);
  align-self: start;
  max-height: calc(100vh - var(--pc-head-h) - 76px);
  overflow-y: auto;
  overscroll-behavior: contain;
  scrollbar-width: thin;
  scrollbar-color: var(--pc-track) transparent;
  padding: 18px 16px 32px;
  background: transparent;
}

.bar {
  position: absolute;
  top: 0;
  left: 16px;
  right: 16px;
  height: 0;
  border-radius: 8px;
  background: var(--pc-active);
  opacity: 0;
  pointer-events: none;
}

.bar::before {
  content: '';
  position: absolute;
  left: 0;
  top: 7px;
  bottom: 7px;
  width: 3px;
  border-radius: 2px;
  background: var(--pc-cyan);
}

.ready .bar {
  transition:
    transform 260ms var(--pc-ease-move),
    height 260ms var(--pc-ease-move),
    width 260ms var(--pc-ease-move);
}

.group {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 18px 14px 6px;
  font-size: 13px;
  color: var(--pc-text-3);
}

.group:nth-child(2) {
  padding-top: 2px;
}

.group kbd {
  height: 20px;
  min-width: 28px;
  font-size: 11px;
}

.tab {
  position: relative;
  display: block;
  padding: 9px 14px;
  border-radius: 8px;
  font-size: 16px;
  font-weight: 500;
  line-height: 1.35;
  color: var(--pc-text-2);
  text-decoration: none;
  transition:
    color 140ms ease,
    background-color 140ms ease;
}

.tab.active {
  color: var(--pc-text);
}

.rail:not(.ready) .bar {
  visibility: hidden;
}

.rail:not(.ready) .tab.active {
  background: var(--pc-active);
  box-shadow: inset 3px 0 0 var(--pc-cyan);
}

.tab:active {
  transform: scale(0.98);
}

.child {
  padding: 7px 14px 7px 30px;
  font-size: 14.5px;
}

.children {
  display: grid;
  grid-template-rows: 0fr;
  transition: grid-template-rows 220ms var(--pc-ease-out);
}

.children > div {
  overflow: hidden;
}

.children.open {
  grid-template-rows: 1fr;
}

@media (hover: hover) and (pointer: fine) {
  .tab:hover {
    color: var(--pc-text);
    background-color: rgba(38, 43, 64, 0.5);
  }

  .tab.active:hover {
    background-color: transparent;
  }
}

@media (max-width: 899px) {
  .rail {
    top: var(--pc-head-h);
    z-index: 15;
    display: flex;
    align-items: center;
    gap: 2px;
    max-height: none;
    padding: 6px 12px;
    overflow-x: auto;
    overflow-y: hidden;
    background: var(--pc-bg);
    border-bottom: 1px solid var(--pc-border);
    scrollbar-width: none;
    -webkit-mask-image: linear-gradient(90deg, #000 calc(100% - 32px), transparent);
    mask-image: linear-gradient(90deg, #000 calc(100% - 32px), transparent);
  }

  .rail::-webkit-scrollbar {
    display: none;
  }

  .group {
    display: none;
  }

  .tab {
    flex: none;
    padding: 9px 12px;
    font-size: 14.5px;
    white-space: nowrap;
  }

  .child {
    padding: 9px 10px;
    font-size: 13.5px;
  }

  .children,
  .children > div {
    display: contents;
  }

  .children:not(.open) .tab {
    display: none;
  }

  .bar {
    top: auto;
    bottom: 0;
    left: 0;
    right: auto;
    width: 0;
    height: 2px;
    border-radius: 2px;
    background: var(--pc-cyan);
  }

  .bar::before {
    display: none;
  }

  .rail:not(.ready) .tab.active {
    background: transparent;
    box-shadow: inset 0 -2px 0 var(--pc-cyan);
  }
}
</style>
