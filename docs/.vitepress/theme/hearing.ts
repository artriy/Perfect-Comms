export type FalloffMode = 'linear' | 'smooth' | 'voiceFocused'
export type OcclusionMode = 'off' | 'softMuffle' | 'softFade' | 'hardBlock' | 'visionOnly'
export type HearingState = 'clear' | 'fading' | 'faint' | 'muffled' | 'wall' | 'quiet' | 'far' | 'dark'

export interface Point {
  x: number
  y: number
}

export interface Wall {
  a: Point
  b: Point
}

export interface HostSettings {
  maxDistance: number
  falloff: FalloffMode
  occlusion: OcclusionMode
  wallsBlock: boolean
  onlyInSight: boolean
}

export interface Heard {
  volume: number
  state: HearingState
}

export const LOCAL_SOFTNESS = 0.3
export const LOW_VOLUME_FLOOR = 0.06
export const LIGHT_RADIUS = 5

export const MAX_DISTANCE_LIMITS = { min: 1.5, max: 20, step: 0.5 }

export const HOST_DEFAULTS: Readonly<HostSettings> = {
  maxDistance: 6,
  falloff: 'smooth',
  occlusion: 'visionOnly',
  wallsBlock: true,
  onlyInSight: true,
}

export const FALLOFF_OPTIONS: { value: FalloffMode; label: string }[] = [
  { value: 'linear', label: 'Linear' },
  { value: 'smooth', label: 'Smooth' },
  { value: 'voiceFocused', label: 'Voice Focused' },
]

export const OCCLUSION_OPTIONS: { value: OcclusionMode; label: string }[] = [
  { value: 'off', label: 'Off' },
  { value: 'softMuffle', label: 'Soft Muffle' },
  { value: 'softFade', label: 'Soft Fade' },
  { value: 'hardBlock', label: 'Hard Block' },
  { value: 'visionOnly', label: 'Vision Only' },
]

export const OCCLUSION_MULTIPLIER: Record<OcclusionMode, number> = {
  off: 1,
  softMuffle: 0.7,
  softFade: 0.35,
  hardBlock: 0,
  visionOnly: 0,
}

export const STATE_WORDS: Record<HearingState, string> = {
  clear: 'Clear',
  fading: 'Fading',
  faint: 'Faint',
  muffled: 'Muffled by a wall',
  wall: 'Behind a wall',
  quiet: 'Too quiet',
  far: 'Too far',
  dark: 'Out of sight',
}

const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value))

export function soften(t: number, softness = LOCAL_SOFTNESS): number {
  return softness <= 0 ? t : t ** (1 + softness * 3)
}

export function falloff(distance: number, maxDistance: number, mode: FalloffMode, softness = LOCAL_SOFTNESS): number {
  if (maxDistance <= 0) return 0
  const t = soften(clamp(distance / maxDistance, 0, 1), softness)
  if (mode === 'linear') return 1 - t
  if (mode === 'smooth') return 1 - t * t * (3 - 2 * t)
  return t < 0.35 ? 1 : ((1 - t) / 0.65) ** 1.35
}

export function effectiveMax(settings: HostSettings): number {
  return settings.onlyInSight ? Math.min(settings.maxDistance, LIGHT_RADIUS) : settings.maxDistance
}

function cross(o: Point, a: Point, b: Point): number {
  return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x)
}

function withinBox(p: Point, q: Point, r: Point): boolean {
  return q.x >= Math.min(p.x, r.x) && q.x <= Math.max(p.x, r.x) && q.y >= Math.min(p.y, r.y) && q.y <= Math.max(p.y, r.y)
}

export function segmentsIntersect(p1: Point, p2: Point, q1: Point, q2: Point): boolean {
  const d1 = cross(q1, q2, p1)
  const d2 = cross(q1, q2, p2)
  const d3 = cross(p1, p2, q1)
  const d4 = cross(p1, p2, q2)
  if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true
  if (d1 === 0 && withinBox(q1, p1, q2)) return true
  if (d2 === 0 && withinBox(q1, p2, q2)) return true
  if (d3 === 0 && withinBox(p1, q1, p2)) return true
  if (d4 === 0 && withinBox(p1, q2, p2)) return true
  return false
}

export function hear(listener: Point, speaker: Point, settings: HostSettings, walls: Wall[]): Heard {
  const max = effectiveMax(settings)
  const distance = Math.hypot(speaker.x - listener.x, speaker.y - listener.y)
  const blocked = walls.some((wall) => segmentsIntersect(listener, speaker, wall.a, wall.b))
  let volume = falloff(distance, max, settings.falloff)
  let state: HearingState | null = null
  let muffled = false

  if (settings.onlyInSight && (blocked || distance > max)) {
    volume = 0
    state = blocked ? 'wall' : distance > LIGHT_RADIUS ? 'dark' : 'far'
  } else if (volume > 0 && settings.wallsBlock && blocked) {
    const multiplier = OCCLUSION_MULTIPLIER[settings.occlusion]
    volume *= multiplier
    muffled = settings.occlusion === 'softMuffle'
    if (multiplier === 0) state = 'wall'
  }

  if (volume < LOW_VOLUME_FLOOR) return { volume: 0, state: state ?? (distance > max ? 'far' : 'quiet') }
  if (muffled) return { volume, state: 'muffled' }
  return { volume, state: volume >= 0.75 ? 'clear' : volume >= 0.35 ? 'fading' : 'faint' }
}
