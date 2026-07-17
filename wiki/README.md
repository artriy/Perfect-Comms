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

# copy public pages (README.md is this source/publishing guide)
for page in PerfectComms/wiki/*.md; do
  [ "$(basename "$page")" = "README.md" ] || cp "$page" Perfect-Comms.wiki/
done
cd Perfect-Comms.wiki
git add -A && git commit -m "docs: update Perfect Comms wiki" && git push
```

## Pages

| File | Page |
| :--- | :--- |
| `Home.md` | Wiki landing |
| `Players.md` | Player guide landing |
| `Installing-Perfect-Comms.md` | Player installation and troubleshooting |
| `Controls.md` | Local settings tabs, controls, and defaults |
| `Host-Settings.md` | Match-wide host settings tabs |
| `Mod-Integration.md` | Mod API landing + setup |
| `Mod-Integration-Examples.md` | Integration examples |
| `Mod-Integration-Gate.md` | Gate, muffle, and player traits |
| `Mod-Integration-Channels.md` | Channels and pair routing |
| `Mod-Integration-Listener-Origin.md` | Listener origin, filter, and phase observer |
| `Mod-Integration-Host-Options.md` | Host options & tabs |
| `Mod-Integration-Overlay-Privacy.md` | Identity-bearing overlay privacy |
| `Mod-Integration-API-Reference.md` | API reference |
| `_Sidebar.md` | Sidebar navigation |
