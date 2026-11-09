import type { Theme } from 'vitepress'
import DefaultTheme from 'vitepress/theme-without-fonts'
import '@fontsource-variable/space-grotesk'
import './style.css'
import PanelLayout from './components/PanelLayout.vue'
import TonightTab from './components/TonightTab.vue'
import HostProximity from './components/HostProximity.vue'
import InviteComposer from './components/InviteComposer.vue'
import HudStatus from './components/HudStatus.vue'

export default {
  extends: DefaultTheme,
  Layout: PanelLayout,
  enhanceApp({ app }) {
    app.component('TonightTab', TonightTab)
    app.component('HostProximity', HostProximity)
    app.component('InviteComposer', InviteComposer)
    app.component('HudStatus', HudStatus)
  },
} satisfies Theme
