<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { withBase } from 'vitepress'
import { data as releases } from '../releases.data'
import { readInvite, regionLabel, startTime, type Invite } from '../invite'
import CopyButton from './CopyButton.vue'
import LobbyRow from './LobbyRow.vue'
import PanelSegmented from './PanelSegmented.vue'
import PanelSelector from './PanelSelector.vue'

type Platform = 'desktop' | 'android'
type Step = 'bepinex' | 'folder' | 'antivirus' | 'game' | 'testing' | 'mic'

const latest = releases[0]
const versionOptions = [...releases].reverse().map((release) => ({ value: release.tag, label: release === latest ? `${release.tag} (latest)` : release.tag }))
const platforms: { value: Platform; label: string }[] = [
  { value: 'desktop', label: 'Desktop' },
  { value: 'android', label: 'Android' },
]
const bepinexChoices = [
  { value: 'have', label: 'I have it' },
  { value: 'none', label: 'No BepInEx folder' },
] as const

const invite = ref<Invite | null>(null)
const mounted = ref(false)
const settled = ref(false)
const now = ref(Date.now())
const chosenTag = ref(latest.tag)
const platform = ref<Platform>('desktop')
const bepinex = ref<'have' | 'none'>('have')
const antivirusOpen = ref(false)
const downloaded = ref(false)
const done = reactive<Record<Step, boolean>>({ bepinex: false, folder: false, antivirus: false, game: false, testing: false, mic: false })

const tag = computed(() => invite.value?.version ?? chosenTag.value)
const release = computed(() => releases.find((entry) => entry.tag === tag.value) ?? null)
const download = computed(() => (platform.value === 'desktop' ? release.value?.desktop : release.value?.android) ?? null)
const fileName = computed(() => download.value?.split('/').pop() ?? '')
const steps = computed<Step[]>(() => (platform.value === 'desktop' ? ['bepinex', 'folder', 'antivirus', 'game'] : ['testing', 'mic']))
const left = computed(() => steps.value.filter((step) => !done[step]).length)
const ready = computed(() => left.value === 0)
const progress = computed(() => (ready.value ? 1 : ((downloaded.value ? 1 : 0) + steps.value.length - left.value) / (steps.value.length + 1)))

const title = computed(() => {
  if (!invite.value) return "Get on voice for tonight's lobby"
  if (invite.value.title) return invite.value.title
  return invite.value.host ? `${invite.value.host}'s lobby` : "Your host's lobby"
})
const when = computed(() => (invite.value?.at ? startTime(invite.value.at, now.value) : null))
const status = computed(() => {
  if (!release.value) return { text: 'Unknown version', tone: 'warn' as const }
  if (ready.value) return { text: 'Ready', tone: 'join' as const }
  if (!downloaded.value && left.value === steps.value.length) return { text: `Needs ${tag.value}`, tone: 'warn' as const }
  return { text: `${left.value} step${left.value > 1 ? 's' : ''} left`, tone: 'step' as const }
})
const joinNote = computed(() => {
  if (status.value.tone === 'warn') {
    return invite.value
      ? `The host admits only ${tag.value}. Without Perfect Comms, or on any other version, you are removed while loading in.`
      : 'A Perfect Comms host admits only their exact version. Without it, or on any other version, you are removed while loading in.'
  }
  if (status.value.tone === 'step') return 'Tick each step below as you finish it.'
  return 'Ready. Your way in is at the bottom of the list.'
})
const joinSteps = computed(() => {
  const lobby = invite.value
  if (!lobby?.code) return 'Open Among Us and join your host’s lobby. F10 opens Voice settings any time.'
  const region = regionLabel(lobby.region)
  return `In Among Us choose Online${region ? `, the ${region} server` : ''}, and enter code ${lobby.code}.`
})

function toggle(step: Step) {
  done[step] = !done[step]
}

watch([tag, platform], () => (downloaded.value = false))

let clock: ReturnType<typeof setInterval> | undefined

onMounted(() => {
  invite.value = readInvite(location.search)
  if (/Android/i.test(navigator.userAgent)) platform.value = 'android'
  mounted.value = true
  setTimeout(() => (settled.value = true), 1000)
  if (!invite.value) return
  document.title = `${title.value} | Perfect Comms`
  if (invite.value.at) clock = setInterval(() => (now.value = Date.now()), 30000)
})

onUnmounted(() => clearInterval(clock))
</script>

<template>
  <section class="tonight" :class="{ mounted, settled, invited: invite }">
    <header class="arrival">
      <p class="kicker">{{ invite ? 'YOU’RE INVITED' : 'PROXIMITY VOICE CHAT FOR AMONG US' }}</p>
      <p v-if="when" class="when">
        <b>{{ when.day }} {{ when.time }}</b> · {{ when.relative }}
      </p>
      <h1 class="title">{{ title }}</h1>
      <p v-if="invite && release" class="needs">
        <template v-if="invite.host">Hosted by {{ invite.host }}. </template>Needs Perfect Comms <b>{{ tag }}</b>, your host’s exact version.
      </p>
      <p v-else-if="invite" class="needs">
        Your host’s link asks for <b>{{ invite.version }}</b>, which is not a published release. Ask your host which version they run, or open
        <a href="https://github.com/artriy/Perfect-Comms/releases">all releases</a>.
      </p>
      <div v-else class="needs pick">
        <span>No Discord, no mute bots: hear the players near you. Everyone needs the host’s exact version.</span>
        <div class="pick-row">
          <span>Your host runs</span>
          <PanelSelector v-model="chosenTag" class="version" :options="versionOptions" label="host version" />
        </div>
        <a class="host-link" :href="withBase('/host')">Hosting tonight? Set your rules, then make an invite link</a>
      </div>
    </header>

    <div v-if="release" class="get">
      <div class="platform">
        <PanelSegmented v-model="platform" :options="platforms" label="Platform" />
        <p class="platform-hint">
          <template v-if="platform === 'desktop'">Windows, Linux (Proton) and macOS (Wine). The plugin DLL only; BepInEx is separate.</template>
          <template v-else>
            Android runs Perfect Comms only through Starlight testing builds and shares voice lobbies with desktop players. Check the first step below before you download.
            <a :href="withBase('/install#android-on-starlight')">How Android works</a>
          </template>
        </p>
      </div>
      <a v-if="download" class="download" :class="{ done: downloaded }" :href="download" @click="downloaded = true">
        <svg class="arrow" viewBox="0 0 20 20" aria-hidden="true"><path class="shaft" d="M10 3v10M5.5 8.5 10 13l4.5-4.5" /><path d="M4 16.5h12" /></svg>
        <span class="label">
          Download {{ tag }}
          <small>{{ fileName }}</small>
        </span>
        <Transition name="pop">
          <span v-if="downloaded" class="downloaded">
            <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5 6.2 11.5 13 4.5" /></svg>
            Downloaded
          </span>
        </Transition>
      </a>
      <p v-else class="download missing">
        {{ tag }} has no Android build. <a :href="release.page">Open the {{ tag }} release</a>
      </p>
      <p class="source">From the <a :href="release.page">{{ tag }} release</a> on GitHub. Perfect Comms is open source.</p>
    </div>

    <template v-if="release">
      <LobbyRow
        class="lobby"
        :title="null"
        :host="invite?.host ?? ''"
        :region="regionLabel(invite?.region ?? null)"
        :code="invite?.code ?? ''"
        :when="null"
        :status="status"
        :progress="progress"
      />
      <p class="join-note">{{ joinNote }}</p>
    </template>

    <div v-if="release" class="pc-rows steps">
      <template v-if="platform === 'desktop'">
        <div class="pc-row step">
          <button type="button" class="tick" :aria-pressed="done.bepinex" aria-label="BepInEx 6 done" @click="toggle('bepinex')">
            <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5 6.2 11.5 13 4.5" /></svg>
          </button>
          <div class="text">
            <div class="pc-name">BepInEx 6</div>
            <div class="pc-hint">Open the folder that contains <code>Among Us.exe</code> (Steam: right-click Among Us, Manage, Browse local files). Is there a <code>BepInEx</code> folder?</div>
          </div>
          <PanelSegmented v-model="bepinex" class="control" :options="[...bepinexChoices]" label="BepInEx folder" />
          <div class="detail">
            <p v-if="bepinex === 'have'">Your mod or modpack already provides it. Keep it, and do not install or merge another copy over it.</p>
            <ol v-else>
              <li>Download <b>BepInEx 6 Unity IL2CPP</b> from the <a href="https://builds.bepinex.dev/projects/bepinex_be">official build page</a>. Choose <code>Unity.IL2CPP-win-x64</code>; Windows x86 is not supported.</li>
              <li>Extract it into the folder with <code>Among Us.exe</code>.</li>
              <li>Launch the game once to finish setup, then close it.</li>
            </ol>
          </div>
        </div>
        <div class="pc-row step">
          <button type="button" class="tick" :aria-pressed="done.folder" aria-label="Plugins folder done" @click="toggle('folder')">
            <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5 6.2 11.5 13 4.5" /></svg>
          </button>
          <div class="text">
            <div class="pc-name">Plugins folder</div>
            <div class="pc-hint">Close Among Us. Put <code>PerfectComms.dll</code> directly in this folder, not a subfolder; replace an older copy.</div>
          </div>
          <span class="path control">
            <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M1.5 4.5v8h13v-6.5h-6.5l-1.5-1.5z" /></svg>
            BepInEx/plugins
          </span>
        </div>
        <div class="pc-row step">
          <button type="button" class="tick" :aria-pressed="done.antivirus" aria-label="Antivirus check done" @click="toggle('antivirus')">
            <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5 6.2 11.5 13 4.5" /></svg>
          </button>
          <div class="text">
            <div class="pc-name">Antivirus</div>
            <div class="pc-hint">On Windows, antivirus can block <code>PerfectCommsAudio.exe</code>, the voice helper inside the DLL.</div>
          </div>
          <button type="button" class="expand control" :aria-expanded="antivirusOpen" aria-controls="antivirus-fix" @click="antivirusOpen = !antivirusOpen">
            Fix
            <svg viewBox="0 0 14 14" aria-hidden="true"><path d="M3 5l4 4 4-4" /></svg>
          </button>
          <div id="antivirus-fix" class="detail fold" :class="{ open: antivirusOpen }" :inert="!antivirusOpen">
            <div>
              <ol>
                <li>Open <b>Windows Security</b>, <b>Virus &amp; threat protection</b>, <b>Protection history</b>.</li>
                <li>Open the entry for <code>PerfectCommsAudio.exe</code> and choose <b>Actions</b>, <b>Restore</b> or <b>Allow on device</b>.</li>
                <li>Back in the game, press <b>Retry</b> on the card or at the top of Voice settings (<kbd>F10</kbd>).</li>
              </ol>
              <a :href="withBase('/troubleshooting#antivirus-blocks-voice-chat')">Other antivirus software and exclusions</a>
            </div>
          </div>
        </div>
        <div class="pc-row step">
          <button type="button" class="tick" :aria-pressed="done.game" aria-label="Game check done" @click="toggle('game')">
            <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5 6.2 11.5 13 4.5" /></svg>
          </button>
          <div class="text">
            <div class="pc-name">Start Among Us</div>
            <div class="pc-hint">First-time setup opens on its own: microphone, controls, HUD. Afterwards <kbd>F10</kbd> opens Voice settings.</div>
          </div>
        </div>
      </template>
      <template v-else>
        <div class="pc-row step">
          <button type="button" class="tick" :aria-pressed="done.testing" aria-label="Starlight testing build done" @click="toggle('testing')">
            <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5 6.2 11.5 13 4.5" /></svg>
          </button>
          <div class="text">
            <div class="pc-name">Starlight testing build</div>
            <div class="pc-hint">
              Public Starlight builds do not load unapproved local mods. Send the DLL to All Of Us staff, or add it with a Starlight beta or local-mod testing build:
              <a href="https://allofus.dev/guides/starlight-dev-guide/#testing">Starlight testing guide</a>.
            </div>
          </div>
        </div>
        <div class="pc-row step">
          <button type="button" class="tick" :aria-pressed="done.mic" aria-label="Microphone permission done" @click="toggle('mic')">
            <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5 6.2 11.5 13 4.5" /></svg>
          </button>
          <div class="text">
            <div class="pc-name">Microphone permission</div>
            <div class="pc-hint">Allow it when Starlight asks. Without it you hear others, but your mic stays off.</div>
          </div>
        </div>
      </template>
      <Transition name="ready">
        <div v-if="ready" class="pc-row ready">
          <span class="ready-mark" aria-hidden="true">
            <svg viewBox="0 0 16 16"><path d="M3 8.5 6.2 11.5 13 4.5" /></svg>
          </span>
          <div class="text">
            <div class="pc-name">{{ invite ? `Ready for ${title}` : 'Ready for tonight' }}</div>
            <div class="pc-hint">{{ joinSteps }}</div>
          </div>
          <CopyButton v-if="invite?.code" class="control" :text="invite.code" label="Copy code" />
        </div>
      </Transition>
    </div>

    <p class="more">
      <a :href="withBase('/install')">Full install guide</a>
      <a :href="withBase('/troubleshooting')">Fix problems</a>
    </p>
  </section>
</template>

<style scoped>
.tonight {
  max-width: 880px;
}

.tonight > :not(.arrival, .steps),
.arrival > *,
.steps > .step {
  animation: pc-rise 560ms var(--pc-ease-out) backwards;
}

.tonight.invited > :not(.arrival, .steps),
.tonight.invited .arrival > *,
.tonight.invited .steps > .step {
  animation-name: pc-rise-invite;
}

.tonight.invited .title {
  animation-name: pc-title-invite;
  animation-duration: 760ms;
}

.tonight > .steps {
  animation: pc-fade 360ms ease-out 330ms backwards;
}

.tonight.invited > .steps {
  animation-name: pc-fade-invite;
}

@keyframes pc-fade {
  from {
    opacity: 0;
  }
}

@keyframes pc-fade-invite {
  from {
    opacity: 0;
  }
}

.arrival > :nth-child(2) {
  animation-delay: 50ms;
}

.arrival > :nth-child(3) {
  animation-delay: 100ms;
}

.arrival > :nth-child(4) {
  animation-delay: 170ms;
}

.tonight > :nth-child(2) {
  animation-delay: 230ms;
}

.tonight > :nth-child(3) {
  animation-delay: 290ms;
}

.tonight > :nth-child(4) {
  animation-delay: 330ms;
}

.tonight > :nth-child(6) {
  animation-delay: 560ms;
}

.steps > .step:nth-child(1) {
  animation-delay: 370ms;
}

.steps > .step:nth-child(2) {
  animation-delay: 420ms;
}

.steps > .step:nth-child(3) {
  animation-delay: 470ms;
}

.steps > .step:nth-child(4) {
  animation-delay: 520ms;
}

.tonight.settled .steps > .step:nth-child(1) {
  animation-delay: 0ms;
}

.tonight.settled .steps > .step:nth-child(2) {
  animation-delay: 40ms;
}

.tonight.settled .steps > .step:nth-child(3) {
  animation-delay: 80ms;
}

.tonight.settled .steps > .step:nth-child(4) {
  animation-delay: 120ms;
}

@keyframes pc-rise {
  from {
    opacity: 0;
    transform: translateY(14px);
  }
}

@keyframes pc-rise-invite {
  from {
    opacity: 0;
    transform: translateY(14px);
  }
}

@keyframes pc-title-invite {
  from {
    opacity: 0;
    transform: translateY(22px);
    filter: blur(8px);
  }
}

.arrival {
  display: grid;
  grid-template-columns: 1fr auto;
  align-items: baseline;
  column-gap: 24px;
}

.kicker {
  margin: 0;
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.14em;
  color: var(--pc-cyan);
}

.when {
  margin: 0;
  font-size: 16px;
  color: var(--pc-text-2);
  font-variant-numeric: tabular-nums;
  text-align: right;
}

.when b {
  font-weight: 600;
  color: var(--pc-text);
}

.title {
  grid-column: 1 / -1;
  margin: 8px 0 12px;
  font-size: 60px;
  font-weight: 600;
  line-height: 1.02;
  letter-spacing: -0.025em;
  overflow-wrap: anywhere;
}

.needs {
  grid-column: 1 / -1;
  margin: 0;
  font-size: 17px;
  line-height: 1.5;
  color: var(--pc-text-2);
}

.needs b {
  font-weight: 600;
  color: var(--pc-text);
}

.needs a {
  color: var(--pc-cyan);
}

.pick {
  display: grid;
  gap: 16px;
}

.pick-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px 14px;
  color: var(--pc-text);
  font-weight: 500;
}

.version {
  width: 260px;
}

.get {
  margin-top: 28px;
}

.platform {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 16px;
  margin-bottom: 12px;
}

.platform-hint {
  margin: 0;
  font-size: 14px;
  color: var(--pc-text-2);
}

.platform-hint a {
  color: var(--pc-cyan);
  white-space: nowrap;
}

.download {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 14px;
  min-height: 76px;
  padding: 10px 24px;
  border-radius: 12px;
  background: var(--pc-cyan);
  color: var(--pc-ink);
  text-decoration: none;
  transition:
    background-color 140ms ease,
    transform 100ms ease;
}

.download:active {
  transform: scale(0.985);
}

.download .arrow {
  width: 26px;
  height: 26px;
  flex: none;
  fill: none;
  stroke: currentColor;
  stroke-width: 2.2;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.download .shaft {
  transform-box: fill-box;
  transition: transform 200ms var(--pc-ease-out);
}

.download.done .shaft {
  animation: pc-drop 560ms var(--pc-ease-out);
}

@keyframes pc-drop {
  45% {
    transform: translateY(8px);
    opacity: 0;
  }

  46% {
    transform: translateY(-8px);
    opacity: 0;
  }
}

.label {
  display: grid;
  font-size: 24px;
  font-weight: 600;
  line-height: 1.15;
}

.label small {
  font-size: 13px;
  font-weight: 500;
  opacity: 0.72;
}

.downloaded {
  position: absolute;
  right: 18px;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 32px;
  padding: 0 12px;
  border-radius: 16px;
  background: rgba(13, 16, 32, 0.14);
  font-size: 14px;
  font-weight: 600;
}

.downloaded svg {
  width: 14px;
  height: 14px;
  fill: none;
  stroke: currentColor;
  stroke-width: 2.6;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.pop-enter-active {
  transition:
    opacity 200ms var(--pc-ease-out),
    transform 200ms var(--pc-ease-out);
}

.pop-enter-from {
  opacity: 0;
  transform: scale(0.9);
}

.missing {
  display: block;
  min-height: 0;
  padding: 18px 22px;
  border: 1px solid var(--pc-border);
  background: var(--pc-row);
  color: var(--pc-text-2);
}

.missing a {
  color: var(--pc-cyan);
}

.lobby {
  margin-top: 20px;
}

.join-note {
  margin: 8px 2px 0;
  font-size: 14px;
  color: var(--pc-text-2);
}

.steps {
  margin-top: 24px;
}

.step {
  grid-template-columns: 28px minmax(0, 1fr) auto;
}

.text {
  min-width: 0;
}

.control {
  justify-self: end;
}

.detail {
  grid-column: 2 / -1;
  font-size: 14.5px;
  color: var(--pc-text-2);
}

.detail p {
  margin: 0;
}

.detail ol {
  margin: 0;
  padding-left: 20px;
  list-style: decimal;
  line-height: 1.7;
}

.detail b {
  font-weight: 600;
  color: var(--pc-text);
}

.detail a,
.pc-hint a {
  color: var(--pc-cyan);
}

.fold {
  display: grid;
  grid-template-rows: 0fr;
  margin-top: -12px;
  transition:
    grid-template-rows 220ms var(--pc-ease-out),
    margin-top 220ms var(--pc-ease-out);
}

.fold > div {
  overflow: hidden;
}

.fold.open {
  grid-template-rows: 1fr;
  margin-top: 0;
}

.fold a {
  display: inline-block;
  margin: 6px 0 2px;
}

code {
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--pc-raised);
  color: var(--pc-text);
  font-family: var(--pc-mono);
  font-size: 0.88em;
}

.tick {
  display: flex;
  align-items: center;
  justify-content: center;
  align-self: start;
  width: 24px;
  height: 24px;
  margin-top: 2px;
  padding: 0;
  border: 1.5px solid var(--pc-track);
  border-radius: 6px;
  background: transparent;
  cursor: pointer;
  transition:
    background-color 160ms ease,
    border-color 160ms ease,
    transform 100ms ease;
}

.tick:active {
  transform: scale(0.9);
}

.tick svg {
  width: 14px;
  height: 14px;
  fill: none;
  stroke: var(--pc-ink);
  stroke-width: 2.6;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-dasharray: 20;
  stroke-dashoffset: 20;
  transition: stroke-dashoffset 200ms var(--pc-ease-out);
}

.tick[aria-pressed='true'] {
  border-color: var(--pc-cyan);
  background: var(--pc-cyan);
}

.tick[aria-pressed='true'] svg {
  stroke-dashoffset: 0;
}

.expand {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  height: 38px;
  padding: 0 14px;
  border: 0;
  border-radius: 8px;
  background: var(--pc-raised);
  color: var(--pc-text);
  font: inherit;
  font-size: 15px;
  font-weight: 500;
  cursor: pointer;
  transition:
    background-color 140ms ease,
    transform 100ms ease;
}

.expand:active {
  transform: scale(0.97);
}

.expand svg {
  width: 14px;
  height: 14px;
  fill: none;
  stroke: currentColor;
  stroke-width: 2;
  stroke-linecap: round;
  stroke-linejoin: round;
  transition: transform 200ms var(--pc-ease-out);
}

.expand[aria-expanded='true'] svg {
  transform: rotate(180deg);
}

.more {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 24px;
  margin: 24px 2px 0;
  font-size: 15px;
}

.more a {
  color: var(--pc-text-2);
  font-weight: 500;
  text-decoration: none;
  transition: color 140ms ease;
}

.host-link {
  justify-self: start;
  color: var(--pc-cyan);
  font-size: 15px;
  font-weight: 500;
  text-decoration: none;
}

.host-link::after {
  content: '\00a0\203a';
}

.source {
  margin: 10px 2px 0;
  font-size: 13.5px;
  color: var(--pc-text-3);
}

.source a {
  color: var(--pc-text-2);
}

.path {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  font-family: var(--pc-mono);
  font-size: 14px;
  color: var(--pc-text);
}

.path svg {
  width: 16px;
  height: 16px;
  fill: none;
  stroke: var(--pc-text-3);
  stroke-width: 1.5;
  stroke-linejoin: round;
}

.ready {
  grid-template-columns: 28px minmax(0, 1fr) auto;
}

.ready .pc-name {
  color: var(--pc-cyan);
  font-weight: 600;
}

.ready-mark {
  display: flex;
  align-items: center;
  justify-content: center;
  align-self: start;
  width: 24px;
  height: 24px;
  margin-top: 2px;
  border-radius: 50%;
  background: var(--pc-cyan);
}

.ready-mark svg {
  width: 14px;
  height: 14px;
  fill: none;
  stroke: var(--pc-ink);
  stroke-width: 2.6;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.ready-enter-active {
  transition:
    opacity 240ms var(--pc-ease-out),
    transform 240ms var(--pc-ease-out);
}

.ready-leave-active {
  transition: opacity 120ms ease;
}

.ready-enter-from {
  opacity: 0;
  transform: translateY(8px);
}

.ready-leave-to {
  opacity: 0;
}

@media (hover: hover) and (pointer: fine) {
  .download:hover {
    background: var(--pc-cyan-hover);
  }

  .download:hover .shaft {
    transform: translateY(2px);
  }

  .tick:hover {
    border-color: var(--pc-text-3);
  }

  .tick[aria-pressed='true']:hover {
    border-color: var(--pc-cyan);
  }

  .expand:hover {
    background: var(--pc-active);
  }

  .more a:hover {
    color: var(--pc-text);
  }
}

@media (max-width: 899px) {
  .title {
    font-size: 40px;
  }
}

@media (max-width: 639px) {
  .arrival {
    grid-template-columns: 1fr;
  }

  .when {
    grid-row: 2;
    margin-top: 4px;
    font-size: 14px;
    text-align: left;
  }

  .kicker {
    font-size: 11px;
  }

  .title {
    margin: 6px 0 8px;
    font-size: 34px;
  }

  .needs {
    font-size: 15px;
  }

  .version {
    width: 100%;
  }

  .get {
    margin-top: 20px;
  }

  .platform-hint {
    font-size: 13px;
  }

  .download {
    min-height: 64px;
    justify-content: flex-start;
    padding: 10px 18px;
  }

  .label {
    font-size: 20px;
  }

  .downloaded {
    right: 12px;
    font-size: 0;
    width: 32px;
    padding: 0;
    justify-content: center;
  }

  .step,
  .ready {
    grid-template-columns: 24px minmax(0, 1fr);
    gap: 8px 12px;
  }

  .control {
    grid-column: 2;
    justify-self: start;
  }

  .detail {
    grid-column: 2;
  }
}
</style>
