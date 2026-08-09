# Performance assessment

## 2026-08-09 baseline

The Tyrian simulation is not the likely Quest bottleneck. A silent desktop
attract-mode sample with the same Godot presentation path measured:

- about 0.16 ms average C# host work per rendered frame (1.54 ms maximum in
  the sampled gameplay window);
- 18 draw calls, 18 visible render objects, and 98 visible snapshot cells;
- about 98.8 MiB reported video memory and 10.8 MiB managed memory;
- no skipped 35 Hz snapshot ticks in the sample.

Those values leave ample CPU and submission headroom. They are not direct Quest
GPU timings, however: desktop flat rendering does not reproduce stereo mobile
Vulkan fill cost.

## Quest-facing cost

OpenTyrian's 320x200 simulation is cheap, but the XR compositor renders two
1680x1760 views. At 4x MSAA that represents roughly 23.7 million color samples
before depth and transparent/depth-prepass overdraw. The scene has up to 22
batched MultiMesh layers plus background, lane, HUD, controls, diagnostics, and
temporary review markers. Typical gameplay activates substantially fewer
layers (18 total draw calls in the flat sample), but MSAA and alpha/depth passes
are the first GPU suspects if Quest misses 72 Hz.

Per-frame snapshot presentation updates each visible cell's MultiMesh transform
and custom data (plus fade color where applicable). At the observed cell count
this was inexpensive. The legacy frame conversion/upload touches 320x200 pixels
at the game's presentation rate and is likewise too small to explain sustained
Quest load.

## On-device telemetry

Version 0.1.5 logs a `PERF` line every five seconds containing:

- render and game presentation rate;
- engine CPU and OpenTyrian host CPU average/maximum time;
- maximum frame interval and count over 16.67 ms;
- draw calls, visible objects/primitives, video and managed memory;
- snapshot cell/visible-instance counts;
- last/maximum simulation-tick gap and cumulative skipped ticks.

The decisive test is two or more minutes of representative Quest gameplay. If
render rate holds 72 Hz with low long-frame and snapshot-gap counts, no quality
reduction is warranted. If host CPU remains low but long frames recur, test 2x
MSAA next; it halves the dominant multisample fill/storage cost while preserving
edge antialiasing. Avoid reducing resolution again: the runtime-recommended 1.0
scale is required for correct stereo presentation on the current mobile path.
