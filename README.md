# OpenTyrianVR

A VR conversion of [OpenTyrian](https://github.com/opentyrian/opentyrian), the
open-source port of the classic DOS shoot-em-up **Tyrian** — rebuilt as a
tilted-lane diorama you look down on, Guitar-Hero style: the level streams
toward you, airborne enemies and shots float above the terrain at real heights,
and the ship is steered by hand position.

The original 320x200 simulation stays fully authoritative — same rules, same
timing, same levels, verified tick-for-tick against the unmodified game with
per-tick state hashes.  Depth is presentation metadata layered on top.

**Status:** in development, not yet playable end-to-end.  See
[docs/VR_CONVERSION_PLAN.md](docs/VR_CONVERSION_PLAN.md) for the plan and
current progress.

## How it works

- The game core builds as a native library and runs unmodified on its own
  thread inside a [Godot 4](https://godotengine.org/) .NET host with OpenXR.
- Each gameplay tick exports a presentation snapshot (every sprite the legacy
  renderer drew, with semantic categories), the background tile maps with
  scroll state, and the 320x200 frame itself.
- The host renders entities as palette-shaded 3D sprites at height bands,
  the terrain as real scrolling tile geometry, and interpolates the 35 Hz
  simulation up to headset refresh rate.
- Hand steering: your left hand moves in a floating control rectangle that
  maps 1:1 onto the playfield; the ship pursues that target inside the
  simulation tick, so it is latency-immune and never overshoots.

## Playing / building

You need the freeware Tyrian 2.1 data files:
<https://camanis.net/tyrian/tyrian21.zip>

Building on Windows is documented in
[docs/BUILDING_WINDOWS.md](docs/BUILDING_WINDOWS.md).  PC VR headsets via
OpenXR (developed against Virtual Desktop / VDXR); a standalone Quest build
via SideQuest is a goal once the conversion is further along.

## Free, forever

This project is free and open source under the
[GPL-2.0](COPYING), like the OpenTyrian port it builds on.  It will never be
paywalled.  If you want to support development:
[ko-fi.com/brobert_m](https://ko-fi.com/brobert_m).

## Credits

- [OpenTyrian](https://github.com/opentyrian/opentyrian) by The OpenTyrian
  Development Team — the port this fork tracks.
- Tyrian by Eclipse Software, published by Epic MegaGames; data files released
  as freeware in 2004.
