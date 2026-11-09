<script setup lang="ts">
import { computed, reactive } from 'vue'
import {
  FALLOFF_OPTIONS,
  HOST_DEFAULTS,
  LIGHT_RADIUS,
  MAX_DISTANCE_LIMITS,
  OCCLUSION_OPTIONS,
  STATE_WORDS,
  effectiveMax,
  hear,
  type HostSettings,
  type Wall,
} from '../hearing'
import PanelSelector from './PanelSelector.vue'
import PanelToggle from './PanelToggle.vue'

const SPAN = 20
const LANE_Y = 0.6
const WALL_AT = 4
const WALLS: Wall[] = [{ a: { x: WALL_AT, y: 0.2 }, b: { x: WALL_AT, y: 2 } }]
const PLAYERS = [
  { name: 'Red', color: '#e5484d', distance: 1, lane: 'down' },
  { name: 'Blue', color: '#4c7dff', distance: 2, lane: 'up' },
  { name: 'Lime', color: '#7ee35b', distance: 3, lane: 'down' },
  { name: 'Pink', color: '#f37ad0', distance: 5, lane: 'up' },
  { name: 'Cyan', color: '#5ce1e6', distance: 7, lane: 'down' },
  { name: 'Orange', color: '#f59a3c', distance: 10, lane: 'up' },
  { name: 'Yellow', color: '#f5e15b', distance: 14, lane: 'down' },
  { name: 'Purple', color: '#a776f0', distance: 18, lane: 'up' },
] as const

const settings = reactive<HostSettings>({ ...HOST_DEFAULTS })
const listener = { x: 0, y: 0 }

const heard = computed(() =>
  PLAYERS.map((player) => ({
    ...player,
    ...hear(listener, { x: player.distance, y: player.lane === 'up' ? LANE_Y : -LANE_Y }, settings, WALLS),
  })),
)
const reach = computed(() => effectiveMax(settings))
const capped = computed(() => reach.value < settings.maxDistance)
const audible = computed(() => heard.value.filter((player) => player.volume > 0).length)
const fill = computed(() => ((settings.maxDistance - MAX_DISTANCE_LIMITS.min) / (MAX_DISTANCE_LIMITS.max - MAX_DISTANCE_LIMITS.min)) * 100)
const at = (distance: number) => `${4 + (Math.min(distance, SPAN) / SPAN) * 92}%`
const ticks = [0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20]
</script>

<template>
  <div class="proximity">
    <div class="pc-rows">
      <div class="pc-row">
        <div>
          <div class="pc-name">Max Distance</div>
          <div class="pc-hint">
            Task-phase hearing distance, 1.5 to 20.0. Default 6.0.
          </div>
        </div>
        <div class="control">
          <div class="slider" :style="{ '--fill': `${fill}%` }">
            <input
              v-model.number="settings.maxDistance"
              type="range"
              :min="MAX_DISTANCE_LIMITS.min"
              :max="MAX_DISTANCE_LIMITS.max"
              :step="MAX_DISTANCE_LIMITS.step"
              aria-label="Max Distance"
            />
          </div>
          <output class="value">{{ settings.maxDistance.toFixed(1) }}</output>
        </div>
      </div>
      <div class="pc-row">
        <div>
          <div class="pc-name">Voice Falloff</div>
          <div class="pc-hint">How voices fade toward the edge of range. Default Smooth.</div>
        </div>
        <PanelSelector v-model="settings.falloff" class="control wide" :options="FALLOFF_OPTIONS" label="Voice Falloff" />
      </div>
      <div class="pc-row">
        <div>
          <div class="pc-name">Voice Occlusion</div>
          <div class="pc-hint">What walls do to voices while Walls Block Audio is on. Default Vision Only.</div>
        </div>
        <PanelSelector v-model="settings.occlusion" class="control wide" :options="OCCLUSION_OPTIONS" label="Voice Occlusion" />
      </div>
      <div class="pc-row">
        <div>
          <div class="pc-name">Walls Block Audio</div>
          <div class="pc-hint">Map walls obstruct voice using Voice Occlusion. Default On.</div>
        </div>
        <PanelToggle v-model="settings.wallsBlock" label="Walls Block Audio" />
      </div>
      <div class="pc-row">
        <div>
          <div class="pc-name">Hear People in Vision Only</div>
          <div class="pc-hint">Hear only players inside your vision. Default On.</div>
        </div>
        <PanelToggle v-model="settings.onlyInSight" label="Hear People in Vision Only" />
      </div>
    </div>

    <figure class="strip">
      <figcaption class="strip-head">
        <span class="strip-title">What you hear</span>
        <span class="count" aria-live="polite">
          {{ audible }} of {{ PLAYERS.length }} heard · reach {{ reach.toFixed(1) }}<template v-if="capped">, capped by Vision Only</template>
        </span>
      </figcaption>
      <div class="lane" role="list">
        <div class="axis" aria-hidden="true" />
        <div class="reach" :style="{ width: `calc(${at(reach)} - 4%)` }" aria-hidden="true" />
        <div v-if="settings.onlyInSight" class="vision" :style="{ left: at(LIGHT_RADIUS) }" aria-hidden="true"><span>Vision</span></div>
        <div class="wall" :style="{ left: at(WALL_AT) }" aria-hidden="true"><span>Wall</span></div>
        <div class="you" aria-hidden="true"><span>You</span></div>
        <div
          v-for="player in heard"
          :key="player.name"
          class="player"
          :class="[player.lane, `is-${player.state}`, { silent: player.volume === 0 }]"
          :style="{ left: at(player.distance) }"
          role="listitem"
        >
          <span class="stem" aria-hidden="true" />
          <span class="chip" :style="{ background: player.color }">{{ player.name }}</span>
          <span class="distance">{{ player.distance }} {{ player.distance === 1 ? 'unit' : 'units' }}</span>
          <span class="meter" aria-hidden="true"><span :style="{ transform: `scaleX(${player.volume})` }" /></span>
          <span class="state">{{ STATE_WORDS[player.state] }}</span>
        </div>
      </div>
      <div class="ticks" aria-hidden="true">
        <span v-for="tick in ticks" :key="tick" :style="{ left: at(tick) }">{{ tick }}</span>
      </div>
      <p class="caption">
        Computed with the mod’s distance, falloff and wall rules. Vision is fixed at 5 units here; in a match it follows the lobby’s vision settings. Each player’s Voice Falloff Softness (default 30%) also shapes the curve.
      </p>
    </figure>
  </div>
</template>

<style scoped>
.proximity {
  margin: 24px 0;
}

.control {
  display: flex;
  align-items: center;
  gap: 10px;
  justify-self: end;
}

.wide {
  width: 300px;
}

.slider {
  position: relative;
  width: 260px;
  height: 24px;
}

.slider input {
  width: 100%;
  height: 100%;
  margin: 0;
  background: transparent;
  cursor: pointer;
  -webkit-appearance: none;
  appearance: none;
}

.slider input::-webkit-slider-runnable-track {
  height: 6px;
  border-radius: 3px;
  background: linear-gradient(90deg, var(--pc-cyan) var(--fill), var(--pc-track) var(--fill));
}

.slider input::-moz-range-track {
  height: 6px;
  border-radius: 3px;
  background: linear-gradient(90deg, var(--pc-cyan) var(--fill), var(--pc-track) var(--fill));
}

.slider input::-webkit-slider-thumb {
  width: 20px;
  height: 20px;
  margin-top: -7px;
  border: 0;
  border-radius: 50%;
  background: #f2f4fa;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.4);
  transition:
    box-shadow 140ms ease,
    transform 100ms ease;
  -webkit-appearance: none;
  appearance: none;
}

.slider input::-moz-range-thumb {
  width: 20px;
  height: 20px;
  border: 0;
  border-radius: 50%;
  background: #f2f4fa;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.4);
  transition:
    box-shadow 140ms ease,
    transform 100ms ease;
}

.slider input:active::-webkit-slider-thumb {
  transform: scale(1.1);
}

.slider input:active::-moz-range-thumb {
  transform: scale(1.1);
}

.slider input:focus-visible {
  outline: none;
}

.slider input:focus-visible::-webkit-slider-thumb {
  box-shadow: 0 0 0 3px var(--pc-cyan);
}

.slider input:focus-visible::-moz-range-thumb {
  box-shadow: 0 0 0 3px var(--pc-cyan);
}

.value {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 72px;
  height: 38px;
  border-radius: 8px;
  background: var(--pc-raised);
  font-size: 16px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}

.strip {
  margin: 16px 0 0;
  padding: 16px 20px 14px;
  border: 1px solid var(--pc-border);
  border-radius: 10px;
  background: var(--pc-row);
}

.strip-head {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 8px;
  font-size: 13px;
  color: var(--pc-text-3);
}

.strip-title {
  font-weight: 500;
  color: var(--pc-text-2);
}

.count {
  font-variant-numeric: tabular-nums;
}

.lane {
  position: relative;
  height: 196px;
}

.axis {
  position: absolute;
  left: 4%;
  right: 4%;
  top: 109px;
  height: 2px;
  border-radius: 1px;
  background: var(--pc-track);
}

.reach {
  position: absolute;
  left: 4%;
  top: 106px;
  height: 8px;
  border-radius: 4px;
  background: linear-gradient(90deg, rgba(106, 203, 236, 0.55), rgba(106, 203, 236, 0.18));
  transition: width 200ms var(--pc-ease-out);
}

.vision {
  position: absolute;
  top: 98px;
  height: 24px;
  border-left: 1px dashed var(--pc-text-3);
}

.vision span {
  position: absolute;
  top: 26px;
  left: 0;
  transform: translateX(-50%);
  font-size: 11px;
  color: var(--pc-text-3);
}

.wall {
  position: absolute;
  top: 88px;
  width: 4px;
  height: 20px;
  margin-left: -2px;
  border-radius: 2px;
  background: var(--pc-text-3);
}

.wall span {
  position: absolute;
  top: 26px;
  left: 50%;
  transform: translateX(-50%);
  font-size: 11px;
  color: var(--pc-text-3);
}

.you {
  position: absolute;
  left: 4%;
  top: 98px;
  transform: translateX(-50%);
}

.you span {
  display: inline-block;
  padding: 3px 9px;
  border-radius: 6px;
  background: var(--pc-text);
  color: var(--pc-ink);
  font-size: 13px;
  font-weight: 600;
}

.player {
  position: absolute;
  display: grid;
  justify-items: center;
  gap: 3px;
  transform: translateX(-50%);
  text-align: center;
}

.player.up {
  top: 20px;
}

.player.down {
  top: 140px;
}

.stem {
  position: absolute;
  left: 50%;
  width: 1px;
  background: var(--pc-border);
}

.up .stem {
  top: 46px;
  height: 43px;
}

.down .stem {
  top: -31px;
  height: 31px;
}

.chip {
  padding: 3px 9px;
  border-radius: 6px;
  color: var(--pc-ink);
  font-size: 13px;
  font-weight: 600;
  white-space: nowrap;
  transition:
    opacity 200ms ease,
    filter 200ms ease;
}

.silent .chip {
  opacity: 0.28;
}

.is-muffled .chip {
  filter: blur(0.6px) saturate(0.7);
}

.distance {
  display: none;
}

.meter {
  width: 44px;
  height: 3px;
  overflow: hidden;
  border-radius: 2px;
  background: var(--pc-track);
}

.meter span {
  display: block;
  height: 100%;
  background: var(--pc-cyan);
  transform-origin: left;
  transition: transform 200ms var(--pc-ease-out);
}

.state {
  font-size: 12px;
  color: var(--pc-text);
  white-space: nowrap;
  transition: color 200ms ease;
}

.is-fading .state,
.is-faint .state,
.is-muffled .state {
  color: var(--pc-text-2);
}

.silent .state {
  color: var(--pc-text-3);
}

.ticks {
  position: relative;
  height: 16px;
  font-size: 11px;
  color: var(--pc-text-3);
  font-variant-numeric: tabular-nums;
}

.ticks span {
  position: absolute;
  transform: translateX(-50%);
}

.caption {
  margin: 10px 0 0;
  font-size: 13px;
  line-height: 1.5;
  color: var(--pc-text-3);
}

@media (max-width: 899px) {
  .pc-row {
    grid-template-columns: minmax(0, 1fr);
  }

  .pc-row:has(.toggle) {
    grid-template-columns: minmax(0, 1fr) auto;
  }

  .control {
    justify-self: stretch;
  }

  .wide {
    width: 100%;
  }

  .slider {
    flex: 1;
    width: auto;
  }
}

@media (max-width: 639px) {
  .strip {
    padding: 12px 14px;
  }

  .lane {
    height: auto;
  }

  .axis,
  .reach,
  .vision,
  .wall,
  .you,
  .stem,
  .ticks {
    display: none;
  }

  .player {
    position: static;
    display: grid;
    grid-template-columns: 76px 1fr 44px;
    grid-template-areas: 'chip distance meter' 'chip state state';
    justify-items: start;
    align-items: center;
    gap: 0 10px;
    padding: 9px 0;
    border-top: 1px solid var(--pc-border);
    transform: none;
    text-align: left;
  }

  .player:first-of-type {
    border-top: 0;
  }

  .chip {
    grid-area: chip;
  }

  .distance {
    display: block;
    grid-area: distance;
    font-size: 12px;
    color: var(--pc-text-3);
    font-variant-numeric: tabular-nums;
  }

  .meter {
    grid-area: meter;
  }

  .state {
    grid-area: state;
    font-size: 13px;
  }
}
</style>
