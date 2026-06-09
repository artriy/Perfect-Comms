This Perfect Comms release fixes the speaking bar showing the wrong player and voice being heard across the whole map, clears an end-game crash that could disconnect you, tones down the end-game group call so it isn't ear-splitting, and trims a few more audio drop-outs.

<p align="center">
  <img src="https://raw.githubusercontent.com/artriy/Perfect-Comms/v2.1.5/assets/brand/divider.svg" alt="divider" width="900">
</p>

### What's Changed

- **The speaking bar shows the right person again.** Fixed a bug where the talking indicator — in meetings and on the in-game bar — could light up the wrong player, or two players' voices could collapse onto a single name, especially after rounds where player slots got reshuffled. Each voice now reliably follows its own player.
- **No more being heard from across the map.** Caused by the same mix-up: a player's voice could ignore distance and come through from anywhere (and, the flip side, a player who was actually nearby could go unheard). Voice now tracks each player's real position again, so proximity works as intended.
- **Fixed a crash/kick around the end-game screen.** Dropping to the end-game results and rejoining the lobby could spam errors and get you disconnected. The voice HUD now steps aside cleanly during that transition, so the freeze-and-kick is gone.
- **The end-game group call is no longer ear-splitting.** On the results screen everyone was played back at full blast; levels are now dialed to a comfortable volume while staying clearly audible.
- **Fewer audio drop-outs and a little less stutter.** Tiny/odd audio frames are now handled cleanly instead of being dropped, and the playback buffer carries a touch more cushion so brief network hiccups don't turn into gaps.
