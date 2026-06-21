Perfect Comms v3.2.2 makes voice smoother and more responsive on shaky connections, fixes the framerate drop in the new Unity Audio mode, and adds a meeting grace period that gives the caller the floor.

<p align="center">
  <img src="https://raw.githubusercontent.com/artriy/Perfect-Comms/v3.2.2/assets/brand/divider.svg" alt="divider" width="900">
</p>

### What's Changed

- **Meeting grace period (new).**
  > <sub>A new host option in the renamed "Meeting & Voice" settings tab. When a meeting or report starts, everyone except the player who called it is briefly muted for a configurable 0 to 15 seconds, so the caller is heard first instead of everyone talking over each other. Set it to 0 to keep the old behavior.</sub>

- **Unity Audio mode no longer tanks your framerate.**
  > <sub>The Unity Audio compatibility mode added in 3.2.1 could drop the game to a near slideshow while you talked. Microphone encoding now runs off the render thread with its per-frame work capped, so framerate stays smooth in Unity Audio mode.</sub>

- **Smoother voice on poor connections.**
  > <sub>Voice now adapts to your link with up to 300ms of jitter buffering on both the sending and receiving sides, so audio holds together through packet loss, lag spikes, and bursty connections instead of breaking up.</sub>

- **More responsive voice.**
  > <sub>The send timing was tightened so voice goes out with less delay (around 20ms down to 5ms in steady state), making conversations feel snappier.</sub>

- **Voice reliability and audio fixes.**
  > <sub>A round of voice-engine fixes: a send-timing race, thread-safe muting, more accurate packet-loss recovery, audio memory-leak cleanup, and corrected meeting-mute timing. On Android, the Interstellar backend no longer produces occasional clicks or doubled audio. Plus assorted memory, HUD, and diagnostics fixes.</sub>
