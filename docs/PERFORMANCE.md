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
and custom data. At the observed cell count
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
- detected rigid-assembly group/seam-guard/plane-locked cell counts;
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

Version 0.1.11 removes per-instance birth/rim opacity and crops terrain and
entities to the 312x184 playable presentation window. Fragment clipping adds a
few comparisons to the existing sprite shaders but removes instance-color
updates; it does not add draw calls or geometry.

Version 0.1.12 corrects the horizontal crop to the actual 264x184 surface and
interpolates all three terrain layers, including ground. Terrain-attached
records reuse the layer's already-computed sub-tick offset; there are no new
draws, meshes, or per-cell searches. Linked boss sections may bridge a maximum
32 px transparent authored gap when aligned, then reuse the existing median
component motion pass.

The 0.1.12 APK was installed but deliberately not launched by automation.
Review of its simulation clamp prompted the 0.1.13 horizontal-width correction.
The next headset pass must validate the revised boundary, smooth ground motion
below floating platforms, linked-boss integrity, cloud transparency, and
sustained 90 Hz motion. Unchecked in-headset checklist items are failures, not
skipped tests.

Version 0.1.13 restores the -24..288 horizontal presentation required by the
simulation's de-parallax 16..280 player clamp while retaining the 0..184
vertical crop. The bounds are now defined once and shared by terrain, entity
clipping/culling, picking, and HUD placement. Cloud identity is latched per map
epoch, so draw-order events cannot switch known cloud art from alpha 0.82 back
to opaque alpha 1.0. This adds no draw calls or per-frame searches.

Version 0.1.16 adds height-driven virtual-sun silhouettes. Entity shadows reuse
the nine existing per-sheet multiplicative MultiMeshes, adding instances but no
new entity draw calls. Elevated map geometry adds two active multiplicative
draws in the representative Episode 1 demo. The deterministic desktop capture
showed 7-16 candidate entity casters and 21 total draws; hidden-window timing is
intentionally throttled and is not a performance baseline. Quest telemetry must
confirm the two added map draws and extra instances remain inside the 90 Hz
budget.

Version 0.1.17 validates every generated entity-shadow fragment against the
two live elevated map tile/atlas pairs. Receiver origins use the visible
layers' interpolated scroll phase, so unsupported portions clip over
transparent cloud/platform holes and the higher receiver wins overlaps. This
adds no draw calls or CPU-built mask texture; only generated-shadow fragments
perform the extra nearest-neighbour coverage reads. The silent desktop gate
observed five entity shadows, two elevated map-shadow passes, both elevated
receiver layers, native flip/storm presentation, and complete legacy fallback.

Version 0.1.26 presents snapshots on the native fixed 35 Hz simulation period
instead of estimating their period from render-thread receipt times. At 90 Hz,
receipt-time sampling aliases into alternating intervals and can create a
persistent terrain sawtooth even when no native snapshot is skipped. The fixed
period removes that source of judder without changing simulation or adding work.
The same build restores the authored -24..288 side lanes by mirroring the
adjacent playable terrain phase, so it does not depend on the discontinuous
legacy tile hidden behind the original HUD.
