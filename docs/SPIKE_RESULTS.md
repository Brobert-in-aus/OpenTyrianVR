# First technical spike — results

Closes VR_CONVERSION_PLAN.md section 6. All measurements from
`tests/otyr_host_harness.c` against `opentyrian-core-x64-Release.dll` (Release,
VS18, SDL2 2.32.10) on the reference machine (RTX 4090, Windows 11).

## Questions the spike was built to answer

**Can the C core be hosted safely in a foreign process?**
Yes. The unmodified game runs on a dedicated 32 MiB-stack thread under SDL's
dummy video driver, with `JE_showVGA()` as the frame/input synchronization
point. In-game quit unwinds the game thread without touching the host process;
session destroy is clean. Menus, demos, and gameplay all work through one
mechanism.

**Does the fixed tick behave under a host?**
Yes. Demo gameplay cadence measured 34.8 fps (avg 28.75 ms, min 26.28, max
30.71 over 696 frames) — the classic 70 Hz / 2 pacing, stable. Hosted
simulation is hash-identical to the desktop build: 887/887 demo ticks matched
state and framebuffer hashes against the desktop baseline log.

**What latency does the boundary add?**
Native input-to-frame latency measured 3–9 ms (avg 5.2 ms) using idle-menu
response timing. Effective input-to-photon is dominated by the game's own
35 Hz tick and the usual render/compositor path, not the boundary.

**Is the Guitar Hero presentation comfortable and workable?**
In-headset verdict (Quest via VirtualDesktopXR 1.0.10, Godot 4.7 .NET host):
comfortable and playable. Lane tuned to 1.0 m wide at 0.9 m distance, 1.05 m
height, 42° tilt. Readability good. Aliasing solved with an anti-aliased
point-sampling shader plus mipmaps, 4x MSAA, and 1.4x render scale. The 35 Hz
frame content is noticeable and unpleasant against a 90 Hz display — accepted
for the spike; Phase 2/3 snapshot interpolation is the fix.

## Decisions taken from the spike

- **Hand-position steering is the target control metaphor**; thumbstick stays
  as fallback/accessibility mode (plan section 7 updated).
- Audio: host-enabled SDL audio through the default output works fine over
  Virtual Desktop streaming; spatialization deferred to the snapshot phase.

## Known debts carried forward

- 35 Hz content on 90+ Hz displays (fix: Phase 2 snapshots + interpolation).
- `session_destroy` detaches the thread if the game ignores quit for 5 s.
- Joystick devices are visible to the hosted game (double-input risk once the
  host also maps gamepads; consider --no-joystick in hosted config).
- The game writes `opentyrian.cfg`/saves to the host's working directory.
