This Perfect Comms release focuses on chat input, Push To Talk, and impostor radio behavior.

<p align="center">
  <img src="https://raw.githubusercontent.com/artriy/Perfect-Comms/v2.0.2/assets/brand/divider.svg" alt="divider" width="900">
</p>

### What's Changed

- Fixed voice keybind behavior while chat is open so toggle shortcuts stay blocked during typing, while push-to-talk and impostor radio can still activate after a short hold.
- Fixed printable push-to-talk and radio keys in chat so quick taps type normally, but held voice keys do not spam characters into the message field.
- Fixed push-to-talk chat handling so it only applies when Mic Mode is set to Push To Talk.
- Fixed impostor radio chat handling so it only applies when the local player can actually use the radio, including blackmailer and jailor voice-block rules.
- Changed Push To Talk mode so the mute toggle no longer creates a redundant manual mute state; released PTT remains muted and holding PTT still transmits.
- Fixed the `VC: mic unavailable` warning appearing during normal Push To Talk idle mute.
