export const REPO = 'https://github.com/artriy/Perfect-Comms'

export interface RailLink {
  text: string
  link: string
  children?: RailLink[]
}

export interface RailGroup {
  label: string
  key?: string
  items: RailLink[]
}

export const RAIL: RailGroup[] = [
  {
    label: 'Tonight',
    items: [
      { text: 'Tonight', link: '/' },
      { text: 'Install', link: '/install' },
      { text: 'Fix problems', link: '/troubleshooting' },
    ],
  },
  {
    label: 'In game',
    key: 'F10',
    items: [
      { text: 'Player guide', link: '/guide' },
      { text: 'Voice settings', link: '/settings' },
      { text: 'OBS', link: '/obs' },
    ],
  },
  {
    label: 'Lobby-wide',
    key: 'F11',
    items: [{ text: 'Host settings', link: '/host' }],
  },
  {
    label: 'Reference',
    items: [
      { text: 'Compatibility', link: '/compatibility' },
      { text: 'Privacy', link: '/privacy' },
      {
        text: 'Mod API',
        link: '/api/',
        children: [
          { text: 'Examples', link: '/api/examples' },
          { text: 'Gate, muffle & traits', link: '/api/gate' },
          { text: 'Channels', link: '/api/channels' },
          { text: 'Listener origin & filter', link: '/api/listener-origin' },
          { text: 'Overlay privacy', link: '/api/overlay-privacy' },
          { text: 'Host options & tabs', link: '/api/host-options' },
          { text: 'API reference', link: '/api/reference' },
        ],
      },
    ],
  },
]

export const pageOf = (link: string) => (link.endsWith('/') ? `${link.slice(1)}index.md` : `${link.slice(1)}.md`)

export const RAIL_ORDER: RailLink[] = RAIL.flatMap((group) => group.items.flatMap((item) => [item, ...(item.children ?? [])]))
