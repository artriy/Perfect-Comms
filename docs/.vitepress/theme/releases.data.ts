import { defineLoader } from 'vitepress'

export interface Release {
  tag: string
  date: string
  page: string
  desktop: string | null
  android: string | null
}

interface GitHubAsset {
  name: string
  browser_download_url: string
}

interface GitHubRelease {
  tag_name: string
  published_at: string
  html_url: string
  draft: boolean
  prerelease: boolean
  assets: GitHubAsset[]
}

declare const data: Release[]
export { data }

const api = 'https://api.github.com/repos/artriy/Perfect-Comms/releases'

export default defineLoader({
  async load(): Promise<Release[]> {
    const headers: Record<string, string> = { Accept: 'application/vnd.github+json', 'User-Agent': 'perfect-comms-docs' }
    if (process.env.GITHUB_TOKEN) headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`
    const releases: GitHubRelease[] = []
    for (let page = 1; ; page++) {
      const response = await fetch(`${api}?per_page=100&page=${page}`, { headers })
      if (!response.ok) throw new Error(`GitHub releases request failed: ${response.status} ${response.statusText}`)
      const batch = (await response.json()) as GitHubRelease[]
      releases.push(...batch)
      if (batch.length < 100) break
    }
    const asset = (release: GitHubRelease, match: (name: string) => boolean) =>
      release.assets.find((file) => match(file.name))?.browser_download_url ?? null
    return releases
      .filter((release) => !release.draft && !release.prerelease)
      .map((release) => ({
        tag: release.tag_name,
        date: release.published_at,
        page: release.html_url,
        desktop: asset(release, (name) => name === 'PerfectComms.dll'),
        android: asset(release, (name) => name.startsWith('PerfectCommsStarlight') && name.endsWith('.dll')),
      }))
      .filter((release) => release.desktop)
  },
})
