import { defineConfig } from 'vitepress'

const slugify = (heading: string) =>
  heading
    .normalize('NFKD')
    .replace(/[\u0300-\u036F]/g, '')
    .replace(/['’]/g, '')
    .replace(/[\u0000-\u001f]/g, '')
    .replace(/[\s~`!@#$%^&*()\-_+=[\]{}|\\;:"“”‘<>,.?/]+/g, '-')
    .replace(/-{2,}/g, '-')
    .replace(/^(\d)/, '_$1')
    .replace(/^-+|-+$/g, '')
    .toLowerCase()

const site = 'https://artriy.github.io/Perfect-Comms/'
const description = 'Proximity voice chat built into Among Us. Install, voice settings, host rules, troubleshooting, and the mod integration API.'

export default defineConfig({
  lang: 'en',
  title: 'Perfect Comms',
  description,
  base: '/Perfect-Comms/',
  cleanUrls: true,
  appearance: 'force-dark',
  lastUpdated: true,
  srcExclude: ['MOD_INTEGRATION.md'],
  sitemap: { hostname: site },
  scrollOffset: { selector: '.scroll-mark', padding: 40 },
  head: [
    ['link', { rel: 'icon', type: 'image/svg+xml', href: '/Perfect-Comms/logo.svg' }],
    ['meta', { name: 'theme-color', content: '#141621' }],
    ['meta', { property: 'og:type', content: 'website' }],
    ['meta', { property: 'og:site_name', content: 'Perfect Comms' }],
    ['meta', { property: 'og:image', content: `${site}og.png` }],
    ['meta', { name: 'twitter:card', content: 'summary_large_image' }],
    ['script', {}, "if (new URLSearchParams(location.search).has('v')) document.documentElement.dataset.invite = ''"],
  ],
  transformPageData(page) {
    const title = page.frontmatter.title ?? page.title
    page.frontmatter.head ??= []
    page.frontmatter.head.push(
      ['meta', { property: 'og:title', content: title ? `${title} | Perfect Comms` : 'Perfect Comms' }],
      ['meta', { property: 'og:description', content: page.frontmatter.description ?? description }],
    )
  },
  markdown: {
    theme: 'tokyo-night',
    anchor: { slugify },
    image: { lazyLoading: true },
    config(md) {
      md.renderer.rules.table_open = () => '<div class="pc-table"><table>\n'
      md.renderer.rules.table_close = () => '</table></div>\n'
      md.core.ruler.push('table_cell_labels', (state) => {
        let headers: string[] = []
        let inHead = false
        let column = 0
        let valueCell = false
        for (const token of state.tokens) {
          if (token.type === 'thead_open') {
            headers = []
            inHead = true
          } else if (token.type === 'thead_close') inHead = false
          else if (token.type === 'tr_open') column = 0
          else if (token.type === 'inline' && inHead) headers.push(token.content.replace(/[*`]/g, '').trim())
          else if (token.type === 'td_open') {
            const label = headers[column++] ?? ''
            token.attrSet('data-label', label)
            valueCell = label === 'Default'
          } else if (token.type === 'inline' && valueCell) {
            const open = new state.Token('html_inline', '', 0)
            open.content = '<span class="pc-value">'
            const close = new state.Token('html_inline', '', 0)
            close.content = '</span>'
            token.children = [open, ...(token.children ?? []), close]
            valueCell = false
          }
        }
      })
      md.core.ruler.push('permalink_labels', (state) => {
        for (const token of state.tokens) {
          const link = token.type === 'inline' ? token.children?.find((child) => child.type === 'link_open' && child.attrGet('class') === 'header-anchor') : undefined
          if (link) link.attrSet('aria-label', `Permalink to "${token.content.replace(/\s*\{#[^}]*\}\s*$/, '').replace(/`/g, '')}"`)
        }
      })
    },
  },
  vite: {
    server: { fs: { allow: ['..'] } },
  },
  themeConfig: {
    search: {
      provider: 'local',
      options: {
        detailedView: true,
        miniSearch: {
          searchOptions: {
            boostDocument: (id: string) => (id.includes('/api/') ? 0.5 : /\/(troubleshooting|install|guide|settings)(#|$)/.test(id) ? 1.6 : 1),
          },
        },
      },
    },
  },
})
