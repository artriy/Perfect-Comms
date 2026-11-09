<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { withBase } from 'vitepress'
import { data as releases } from '../releases.data'
import { HOST_LIMIT, REGIONS, TITLE_LIMIT, inviteQuery, regionLabel, startTime, type Invite, type Region } from '../invite'
import CopyButton from './CopyButton.vue'
import LobbyRow from './LobbyRow.vue'
import PanelSelector from './PanelSelector.vue'

const versionOptions = [...releases].reverse().map((release) => ({ value: release.tag, label: release === releases[0] ? `${release.tag} (latest)` : release.tag }))
const regionOptions = [{ value: '', label: 'Not set' }, ...REGIONS.map((region) => ({ value: region.value, label: region.label }))]

const title = ref('')
const host = ref('')
const code = ref('')
const region = ref<Region | ''>('')
const start = ref('')
const version = ref(releases[0].tag)
const origin = ref('')
const now = ref(0)

const codeValid = computed(() => /^([A-Z]{4}|[A-Z]{6})?$/.test(code.value))
const invite = computed<Invite>(() => ({
  title: title.value.trim(),
  host: host.value.trim(),
  code: codeValid.value ? code.value : '',
  region: region.value || null,
  at: start.value ? new Date(start.value).toISOString() : null,
  version: version.value,
}))
const when = computed(() => (invite.value.at && now.value ? startTime(invite.value.at, now.value) : null))
const link = computed(() => `${origin.value}${withBase('/')}?${inviteQuery(invite.value)}`)
const previewTitle = computed(() => invite.value.title || (invite.value.host ? `${invite.value.host}'s lobby` : "Your host's lobby"))

function onCode(event: Event) {
  const input = event.target as HTMLInputElement
  code.value = input.value.toUpperCase().replace(/[^A-Z]/g, '')
  input.value = code.value
}

function localInput(date: Date) {
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`
}

onMounted(() => {
  origin.value = location.origin
  now.value = Date.now()
  const next = new Date()
  next.setHours(next.getHours() + 1, 0, 0, 0)
  start.value = localInput(next)
})
</script>

<template>
  <div class="composer">
    <div class="fields">
      <label class="field title">
        <span class="field-label">Lobby title <span class="count">{{ title.length }}/{{ TITLE_LIMIT }}</span></span>
        <input v-model="title" :maxlength="TITLE_LIMIT" placeholder="Skeld chaos night" autocomplete="off" />
      </label>
      <label class="field name">
        <span class="field-label">Your name</span>
        <input v-model="host" :maxlength="HOST_LIMIT" placeholder="Sam" autocomplete="off" />
      </label>
      <label class="field code">
        <span class="field-label">
          Code
          <span v-if="!codeValid" class="warn">6 letters</span>
        </span>
        <input :value="code" class="mono" maxlength="6" placeholder="ABCDEF" autocomplete="off" spellcheck="false" @input="onCode" />
      </label>
      <label class="field starts">
        <span class="field-label">Starts</span>
        <input v-model="start" type="datetime-local" />
      </label>
      <div class="field">
        <span class="field-label">Region</span>
        <PanelSelector v-model="region" class="pick" :options="regionOptions" label="region" />
      </div>
      <div class="field">
        <span class="field-label">Perfect Comms version</span>
        <PanelSelector v-model="version" class="pick" :options="versionOptions" label="version" />
      </div>
    </div>

    <p class="preview-label">Your guests see</p>
    <LobbyRow
      :title="previewTitle"
      :host="invite.host"
      :region="regionLabel(invite.region)"
      :code="invite.code"
      :when="when ? `${when.day} ${when.time}` : null"
      :status="{ text: `Needs ${version}`, tone: 'warn' }"
    />
    <p class="preview-note">The link opens Tonight with this lobby, a Download for {{ version }}, and the install steps.</p>

    <div class="actions">
      <CopyButton :text="link" label="Copy invite link" primary />
      <a class="open" :href="link" target="_blank" rel="noopener">Open as a guest</a>
    </div>
  </div>
</template>

<style scoped>
.composer {
  margin: 20px 0 8px;
  padding: 20px;
  border: 1px solid var(--pc-border);
  border-radius: 10px;
  background: var(--pc-row);
}

.fields {
  display: grid;
  grid-template-columns: repeat(12, minmax(0, 1fr));
  gap: 14px 12px;
}

.field {
  display: grid;
  gap: 6px;
  grid-column: span 6;
  min-width: 0;
}

.field.title {
  grid-column: span 8;
}

.field.name,
.field.code {
  grid-column: span 4;
}

.field.starts {
  grid-column: span 8;
}

.field-label {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  font-size: 13px;
  color: var(--pc-text-2);
}

.count {
  color: var(--pc-text-3);
  font-variant-numeric: tabular-nums;
}

.warn {
  color: var(--pc-warning);
}

input {
  width: 100%;
  height: 40px;
  padding: 0 12px;
  border: 1px solid var(--pc-border);
  border-radius: 8px;
  background: var(--pc-raised);
  color: var(--pc-text);
  font: inherit;
  font-size: 15px;
  color-scheme: dark;
  transition: border-color 140ms ease;
}

input::placeholder {
  color: var(--pc-text-3);
}

input:focus {
  outline: none;
  border-color: var(--pc-cyan);
}

.mono {
  font-family: var(--pc-mono);
  letter-spacing: 0.08em;
}

.pick {
  width: 100%;
}

.preview-label {
  margin: 22px 0 8px;
  font-size: 13px;
  color: var(--pc-text-2);
}

.preview-note {
  margin: 8px 2px 0;
  font-size: 13.5px;
  color: var(--pc-text-3);
}

.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 12px 20px;
  margin-top: 18px;
}

.open {
  color: var(--pc-text-2);
  font-weight: 500;
  text-decoration: none;
  transition: color 140ms ease;
}

@media (hover: hover) and (pointer: fine) {
  input:hover:not(:focus) {
    border-color: var(--pc-text-3);
  }

  .open:hover {
    color: var(--pc-text);
  }
}

@media (max-width: 899px) {
  .fields {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .field,
  .field.name,
  .field.code,
  .field.starts {
    grid-column: span 1;
  }

  .field.title {
    grid-column: 1 / -1;
  }
}

@media (max-width: 639px) {
  .composer {
    padding: 14px;
  }

  .fields {
    grid-template-columns: minmax(0, 1fr);
  }

  .field,
  .field.name,
  .field.code,
  .field.starts,
  .field.title {
    grid-column: 1 / -1;
  }
}
</style>
