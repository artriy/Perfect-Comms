# Wiki source

These markdown files are the source for the GitHub wiki at
<https://github.com/artriy/Perfect-Comms/wiki>.

GitHub stores wikis in a separate git repo (`<repo>.wiki.git`) with flat `.md`
files; page links use the file name without extension (e.g. `[Gate](Mod-Integration-Gate)`
resolves to `Mod-Integration-Gate.md`). `_Sidebar.md` renders as the sidebar.

## Publish / update

```bash
# one-time clone of the wiki repo (must have at least one page created in the UI first)
git clone https://github.com/artriy/Perfect-Comms.wiki.git

# copy these pages in and push
cp PerfectComms/wiki/*.md Perfect-Comms.wiki/
cd Perfect-Comms.wiki
git add -A && git commit -m "docs: mod integration wiki" && git push
```

## Pages

| File | Page |
| :--- | :--- |
| `Home.md` | Wiki landing |
| `Mod-Integration.md` | Mod API landing + setup |
| `Mod-Integration-Gate.md` | Gate: mute & muffle |
| `Mod-Integration-Channels.md` | Channels: private & team radio |
| `Mod-Integration-Listener-Origin.md` | Listener origin |
| `Mod-Integration-Host-Options.md` | Host options & tabs |
| `Mod-Integration-API-Reference.md` | API reference |
| `_Sidebar.md` | Sidebar navigation |

Player pages (`Installing-Perfect-Comms`, `Host-Settings`, `Controls`) are linked
from the sidebar but not yet authored here - create them in the UI or add them later.
