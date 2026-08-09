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
are the first GPU suspects if Quest misses the requested 90 Hz.

Per-frame snapshot presentation updates each visible cell's MultiMesh transform
and custom data (plus fade color where applicable). At the observed cell count
this was inexpensive. The legacy frame conversion/upload touches 320x200 pixels
at the game's presentation rate and is likewise too small to explain sustained
Quest load.

## On-device telemetry

Version 0.1.5 logs a `PERF` line every five seconds containing:

- render and game presentation rate;
- engine CPU and OpenTyrian host CPU average/maximum time;
- maximum frame interval and count over the active budget (12 ms in XR,
  allowing a small margin over the 90 Hz 11.11 ms interval);
- draw calls, visible objects/primitives, video and managed memory;
- snapshot cell/visible-instance counts;
- detected rigid-assembly group/seam-guard cell counts;
- last/maximum simulation-tick gap and cumulative skipped ticks.

The decisive test is two or more minutes of representative Quest gameplay. If
render rate holds 90 Hz with low long-frame and snapshot-gap counts, no quality
reduction is warranted. If host CPU remains low but long frames recur, test 2x
MSAA next; it halves the dominant multisample fill/storage cost while preserving
edge antialiasing. Avoid reducing resolution again: the runtime-recommended 1.0
scale is required for correct stereo presentation on the current mobile path.

## Quest result (0.1.5)

The sustained headset pass held 72 Hz while gameplay presented at 34.5 Hz.
Representative five-second windows measured about 4.77 ms engine CPU time and
1.12 ms average / 2.91 ms maximum OpenTyrian host work, with a 13.89 ms maximum
frame interval and no long frames. The scene submitted 5 draws, 5 objects and
about 6,916 primitives, using 289.8 MiB reported video memory and 26.4 MiB
managed memory. Approximately 85 of 90 snapshot cells were visible.

There were two skipped simulation snapshots around startup/menu transitions,
not recurring gameplay misses. One menu/checklist transition produced two long
frames (63.89 ms maximum); it did not repeat during play. The device pass is a
performance pass: keep 4x MSAA and runtime-recommended resolution. Version
0.1.7 adds per-window player, raw-hand and target X ranges to `PERF` lines so
edge-travel reports can be confirmed from headset logs.

Version 0.1.8 (ABI v25) generalizes composite stabilization beyond intrinsic
2x2 enemies. Snapshot records carry the full native `linknum` separately from
their stable source identity. A host-side union-find groups only spatially
connected records that share an exact source or nonzero assembly id; members use one
median interpolation delta and a half-pixel conservative join guard. The pass
is O(n squared) in snapshot records (typically about 90 cells on Quest), does
not add draw calls, and logs group/cell counts for device confirmation.

## Quest 90 Hz target (0.1.9)

The validated 72 Hz pass left enough measured CPU and submission headroom to
request 90 Hz while retaining 4x MSAA and runtime-recommended resolution. On
Android, the OpenXR host now logs the runtime's available refresh rates, requests
90 Hz at initialization, then reapplies and logs the actual selected rate after
the first few rendered frames. The headset log is authoritative: a 90 Hz request
does not imply acceptance on every runtime or power/thermal state.

At 90 Hz the frame interval is 11.11 ms. XR telemetry therefore counts intervals
over 12 ms as long frames. The next headset pass should confirm an actual 90 Hz
selection, near-90 render rate, and no recurring judder or long-frame clusters.
