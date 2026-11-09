<script setup lang="ts">
import { nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useData } from 'vitepress'

interface Heading {
  id: string
  text: string
  level: number
}

const { page } = useData()
const headings = ref<Heading[]>([])
const activeId = ref('')
const root = ref<HTMLElement | null>(null)
const list = ref<HTMLElement | null>(null)
const barTop = ref(0)
const barHeight = ref(0)

function collect() {
  headings.value = [...document.querySelectorAll<HTMLElement>('.pc-doc h2[id], .pc-doc h3[id]')].map((el) => ({
    id: el.id,
    text: [...el.childNodes]
      .filter((node) => !(node instanceof HTMLElement && node.classList.contains('header-anchor')))
      .map((node) => node.textContent)
      .join('')
      .trim(),
    level: el.tagName === 'H2' ? 2 : 3,
  }))
  track()
}

let frame = 0
function track() {
  cancelAnimationFrame(frame)
  frame = requestAnimationFrame(() => {
    if (!root.value) return
    const line = parseFloat(getComputedStyle(root.value).getPropertyValue('--pc-head-h')) + 48
    let id = headings.value[0]?.id ?? ''
    for (const heading of headings.value) {
      const el = document.getElementById(heading.id)
      if (el && el.getBoundingClientRect().top <= line) id = heading.id
    }
    if (innerHeight + scrollY >= document.documentElement.scrollHeight - 2 && headings.value.length) id = headings.value[headings.value.length - 1].id
    activeId.value = id
    nextTick(() => {
      const link = list.value?.querySelector<HTMLElement>(`[data-id="${CSS.escape(id)}"]`)
      if (!link) return
      barTop.value = link.offsetTop
      barHeight.value = link.offsetHeight
    })
  })
}

watch(
  () => page.value.relativePath,
  () => nextTick(() => requestAnimationFrame(collect)),
)

onMounted(() => {
  collect()
  addEventListener('scroll', track, { passive: true })
  addEventListener('resize', track)
})

onUnmounted(() => {
  cancelAnimationFrame(frame)
  removeEventListener('scroll', track)
  removeEventListener('resize', track)
})
</script>

<template>
  <aside ref="root" class="outline" aria-labelledby="outline-title">
    <template v-if="headings.length > 1">
      <p id="outline-title" class="title">On this page</p>
      <nav ref="list" class="list">
        <span class="bar" :style="{ transform: `translateY(${barTop}px)`, height: `${barHeight}px` }" aria-hidden="true" />
        <a
          v-for="heading in headings"
          :key="heading.id"
          :data-id="heading.id"
          :href="`#${heading.id}`"
          :class="['link', `level-${heading.level}`, { active: heading.id === activeId }]"
          >{{ heading.text }}</a
        >
      </nav>
    </template>
  </aside>
</template>

<style scoped>
.outline {
  position: sticky;
  top: var(--pc-head-h);
  align-self: start;
  max-height: calc(100vh - var(--pc-head-h));
  overflow-y: auto;
  scrollbar-width: none;
  padding: 40px 24px 32px 8px;
}

.title {
  margin: 0 0 10px 14px;
  font-size: 13px;
  color: var(--pc-text-3);
}

.list {
  position: relative;
  display: grid;
  border-left: 1px solid var(--pc-border);
}

.bar {
  position: absolute;
  left: -1px;
  top: 0;
  width: 2px;
  border-radius: 2px;
  background: var(--pc-cyan);
  transition:
    transform 220ms var(--pc-ease-move),
    height 220ms var(--pc-ease-move);
}

.link {
  padding: 5px 0 5px 14px;
  font-size: 13.5px;
  line-height: 1.4;
  color: var(--pc-text-3);
  text-decoration: none;
  transition: color 140ms ease;
}

.link.level-3 {
  padding-left: 26px;
  font-size: 13px;
}

.link.active {
  color: var(--pc-text);
}

@media (hover: hover) and (pointer: fine) {
  .link:hover {
    color: var(--pc-text);
  }
}
</style>
