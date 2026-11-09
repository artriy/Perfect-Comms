export const REGIONS = [
  { value: 'na', label: 'North America' },
  { value: 'eu', label: 'Europe' },
  { value: 'as', label: 'Asia' },
] as const

export type Region = (typeof REGIONS)[number]['value']

export interface Invite {
  title: string
  host: string
  code: string
  region: Region | null
  at: string | null
  version: string
}

export const TITLE_LIMIT = 40
export const HOST_LIMIT = 10

export const regionLabel = (region: Region | null) => REGIONS.find((entry) => entry.value === region)?.label ?? null

export function readInvite(search: string): Invite | null {
  const params = new URLSearchParams(search)
  const version = params.get('v')
  if (!version) return null
  const code = (params.get('c') ?? '').toUpperCase()
  const at = params.get('at')
  return {
    title: (params.get('t') ?? '').trim().slice(0, TITLE_LIMIT),
    host: (params.get('h') ?? '').trim().slice(0, HOST_LIMIT),
    code: /^([A-Z]{4}|[A-Z]{6})$/.test(code) ? code : '',
    region: REGIONS.find((entry) => entry.value === params.get('r'))?.value ?? null,
    at: at && !Number.isNaN(Date.parse(at)) ? new Date(at).toISOString() : null,
    version,
  }
}

export function inviteQuery(invite: Invite): string {
  const params = new URLSearchParams()
  if (invite.title) params.set('t', invite.title)
  if (invite.host) params.set('h', invite.host)
  if (invite.code) params.set('c', invite.code)
  if (invite.region) params.set('r', invite.region)
  if (invite.at) params.set('at', invite.at)
  params.set('v', invite.version)
  return params.toString()
}

const sameDay = (a: Date, b: Date) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate()

export function startTime(at: string, now: number) {
  const start = new Date(at)
  const today = new Date(now)
  const tomorrow = new Date(now)
  tomorrow.setDate(today.getDate() + 1)
  const time = start.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })
  const day = sameDay(start, today)
    ? start.getHours() >= 17 ? 'Tonight' : 'Today'
    : sameDay(start, tomorrow)
      ? 'Tomorrow'
      : start.toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short' })
  const minutes = Math.round((start.getTime() - now) / 60000)
  const span = (total: number) => {
    if (total < 60) return `${total} min`
    if (total < 1440) return `${Math.floor(total / 60)} h${total % 60 ? ` ${total % 60} min` : ''}`
    const days = Math.round(total / 1440)
    return `${days} day${days > 1 ? 's' : ''}`
  }
  const relative = minutes > 0 ? `starts in ${span(minutes)}` : minutes === 0 ? 'starting now' : `started ${span(-minutes)} ago`
  return { day, time, relative }
}
