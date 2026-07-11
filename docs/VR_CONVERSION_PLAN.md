# OpenTyrian VR conversion plan

## Status (2026-07-10)

- Phase 0: **complete** (fork/pin, reproducible build, replay + per-tick
  hashes with turbo verification, taxonomy doc, license review).
- Section 6 spike: **complete**, results in SPIKE_RESULTS.md.
- Phase 1: **complete** (played in-headset; hand-rectangle steering with
  in-sim target pursuit became the primary control).
- Phase 2: **complete** (GameInput, re-entrant JE_levelTick, all update/draw
  splits via the presentation frame, sound events, snapshot ABI v7 with
  sprite-sheet export and per-record source ids).
- Phase 3 vertical slice: **flat-verified** — entities render as interpolated
  palette-shaded 3D sprites at taxonomy height bands over an
  entity-suppressed terrain lane; terrain-baked art stays in the frame.
  The lane background is now real 3D tile geometry (ABI v8): the level's
  three map layers export with per-tick scroll records (bit-exact, verified
  by standalone raster hashes in the harness); ground/structures render
  pixel-locked behind the color-keyed legacy overlay, clouds elevated with
  scroll interpolation. Old-table blend shots (special weapons) render as
  interpolated 3D quads from the OPTION_SHAPES export (ABI v9). Statics and
  shadows render as zero-parallax terrain decals (depth-bias paint order);
  palette gamma, color keying, and shadow rendering verified in-headset
  across eight test passes. Known gaps for Stage B authored metadata:
  enemy hover heights (movers ride a uniform hazard band, grounded units
  float); and the level-1 dome artifact: a cell-aligned square at the
  dome's top-middle that shimmers/vanishes in-headset. Established facts:
  the art (enemy sheet 5, cells ~97-107, captures/sheet5_annotated.png) is
  FULLY OPAQUE (tester-verified from the sheet dump); the assembly is
  multi-slot; the mechanical verifier shows the legacy frame disagreeing
  with cells 105/106 at their recorded positions (~34-48% match, meaning
  legacy paints something else there); and the artifact does NOT reproduce
  in static flat self-captures of the same build (OTYR_CAPTURE) -- it is
  temporal and/or head-parallax dependent. Candidate for Stage B
  single-quad assembly rendering. In-play overlay text and HUD icons
  (cash, lives, WARNING, timer, game over, insert coin) now render PROUD
  of the playfield at 0.090 (ABI v13): fonthand glyph draws and the
  JE_inGameDisplays icon blits record as OTYR_CAT_TEXT inside a per-tick
  window (open at present_frame_reset, closed at JE_showVGA, gated to
  game_screen and the play region, so pause/menu/sidebar/bottom-bar text
  stays in the frame), the font tables export through the old-sprite
  cache, and the host hue/value-shades glyphs in-shader (including the
  legacy unsafe value wrap). Drop shadows are multiplicative glyph
  quads on a sub-plane 0.0008 below the glyphs (a one-OrderBias gap
  quantized to equal depth and flickered against the unordered shadow
  node). Atlas-slot shader decode rounds before mod/floor: slots at
  exact multiples of the atlas row width (column 0) arrived a hair
  under the integer and sampled empty atlas space, deleting specific
  glyphs (the S/C of INSERT COIN); the old-table blend-shot shader had
  the same latent bug. Verified: turbo hash gate bit-identical (28,960
  ticks), harness glyph-vs-frame verification clean, flat captures show
  the full string with shadows. Awaiting headset pass. One unreproduced sighting of non-cloaking enemies rendering
  semi-transparent for part of a run.
- Phase 4 kickoff (2026-07-11, autonomous slice): 2x2 sprites render as a
  SINGLE 24x28 quad (the shader picks the legacy cell -- +0/+1/+19/+20 --
  by UV quadrant): one pairing/interpolation/band decision per sprite, so
  cells can no longer shear against each other (the dome-square/wedge
  artifact class). Smoothie-warp levels fall back per tick to full legacy
  drawing: effective suppression is recomputed at the top of each tick,
  and OtyrFrame.legacy_fallback (v14) tells the host to hide its 3D
  layers and show the complete flat frame (verified with the
  OTYR_FORCE_SMOOTHIE debug env). Presentation-mode inventory across the
  five shipped demos (TYRIAN 9, SAVARA 12, SOH JIN 13, MINES 7,
  DELIANI 5): no demo level uses smoothies; background3over==2 is live on
  SAVARA and SOH JIN; background2over is only ever 0/1. FINDING for
  Stage B: background3over==1 (TYRIAN late, SAVARA, DELIANI) is a
  FOREGROUND draw -- layer 3 paints over the player and all shots
  (tyrian2.c ~1514) -- but the host maps it to the platform plane
  (0.030, under the player); over modes also CHANGE mid-level via
  events, so honoring them as heights needs a transition treatment
  (relates to the reported "layer jumping"). Tooling: OTYR_CAPTURE=N
  (capture count), OTYR_MUTE=1 (silent solo runs), and the harness bg
  verification is epoch-aware (OTYR_BG_SWEEP=secs spans the whole demo
  cycle, refetching maps per level).
- Headset pass (2026-07-11): text-proud VERIFIED (cash, level-start
  flash, pause remnant gone, sidebar/bottom bar flat); dome nearly
  correct under single-quad (two pairs of transparent pixels remain on
  the lower section -- possibly genuine art transparency, visible only
  because the structure floats; folds into the Stage B height work).
  Menu text stays flat by design until the Phase 4 menu redesign.
  REGRESSION found and fixed: the player ship smeared/shredded --
  EXPLOSION slot ids recycle every 3-12 ticks, so a recycled slot
  paired a new burst with a dead one within the 16px radius and the
  quad slid across whatever it followed; the player-following sparkles
  smeared translucent explosion art over the ship every frame (the
  long-standing "speckle", writ large once 2x2s became single quads).
  Explosion cells no longer interpolate (bursts live 3-12 ticks, drift
  ~1px/tick; stepping imperceptible) -- this retires the speckle class.
  Debug: OTYR_FORCE_SHIP=<id> substitutes player 1's ship in demos;
  solo Godot runs need CLI --xr-mode off or the engine hijacks an awake
  headset regardless of OTYR_FLAT.
- Headset round 2 (2026-07-11): explosion fix held (speckle and sliding
  bursts PASSED) but the ship stayed ghosted with a solid-color core,
  the special-weapon HUD icon drew as a solid bar, and 2x2 flyers went
  translucent over platforms. ROOT CAUSE (whole family): per-instance
  custom data was read through SMOOTH varyings -- barycentric
  interpolation of four identical 8.0s can yield 7.9998, and at
  power-of-two decode boundaries that flips flag bits (floor(x/8) loses
  the 2x2 bit -> one cell stretched; floor(x/2) gains phantom blend ->
  55% alpha; mod(x,2) gains phantom filter -> hue-forced solid colors).
  The INSERT COIN column-0 glyph loss was the same mechanism via
  mod(304-eps,16). Pipeline-dependent (flat desktop often exact, VR
  multiview not), which is why solo captures could pass while the
  headset failed. Fix: ALL per-instance varyings are now `flat`
  (exact provoking-vertex values) across the six snapshot shaders;
  the rounded decodes stay as belt-and-braces. Also fixed: the
  level-complete glow text ramps INTO palette index 254 (set_colors
  maps it to white) while the level color key was still armed, so the
  brightest glyph pixels were keyed out ("flashes to black");
  JE_endLevelAni now ends the level presentation (otyr_host_level_end)
  before drawing.
- Headset round 3 (2026-07-11): ship solid (flat-varying fix confirmed
  in-headset). Fixed: pause/in-game menu run mid-tick and their presents
  closed the in-play text window, so ONE tick of HUD blits leaked into
  the frame and froze under the pause backdrop (the "orange rectangle");
  the window reopens before the tick's draw phase. Investigated the
  "ghost player duplicate" at top-left: it is the SPECIAL-WEAPON HUD
  icon rendering CORRECTLY -- Astral Zone's art is literally a small
  ship + green ? + swirl + red core with transparent gaps, it floats
  proud (0.09) over the void corner, parallaxes with the head, and its
  art changes when arcade powerups swap the special ("blue orbs
  sometimes"). Legibility of corner HUD icons over the void is a
  Phase 4 spatial-HUD concern, not a defect. OPEN (Stage B): first
  spawn of a to-be-mover (the purple carrier) classifies as
  terrain-baked art until it first moves (never-moved + opaque-cell
  heuristic) and renders as a ground decal -- reads as translucent vs
  its later solid spawns; the ground-classification pass owns this.
  OPEN (cosmetic): sub-art-pixel sliver at the ship quad's top/left
  edges, needs a dedicated look. Debug envs: OTYR_FORCE_ARCADE,
  OTYR_FORCE_SPECIAL join OTYR_FORCE_SHIP for demo-based HUD repro.
- Final look (2026-07-11 evening): the tally screen turned WHITE -- the
  round-3 keying fix disarmed the key while the suppressed-background
  fill (index 254) was still in the frame, and the glow palette maps
  254 to white; JE_endLevelAni now paints those pixels black (the 3D
  scene they keyed out is gone). The pause-backdrop pip persists
  despite the window-reopen fix -- origin still unknown; a tripwire in
  the text gate (OTYR_TRACE "hudleak-window"/"hudleak-surface" lines
  with coordinates) will identify any flat in-play HUD draw next
  session. Ship edge sliver reference saved to
  captures/ship_edge_sliver.png (4K in-headset crop: hairline dashes
  off the quad's top/left edges, sub-art-pixel scale; suspects are the
  half-texel edge clamp under MSAA and the shadow quad's border).
- Autonomous round (2026-07-11 late): TWO fixes awaiting headset
  confirmation.  (1) Ship-edge sliver root cause: the 2x2 single-quad
  shaders decoded the UV quadrant with fract(uv0*2.0) -- MSAA edge
  fragments get UVs extrapolated a hair outside [0,1], and fract wraps
  a small negative to ~1.0, sampling the FAR edge of the sub-cell
  (opaque mid-sprite art) instead of the transparent border.  That is
  why the dashes sat on the top/left edges only (positive overshoot
  lands on transparent borders and discards) and why the artifact
  arrived with the single-quad round.  Sprite and shadow shaders now
  clamp instead of wrapping; flat captures verify 2x2 assembly intact.
  (2) Purple-carrier first-spawn: the mover latch now trips on motion
  CAPABILITY (exc/eyc/excc/eycc/xaccel/yaccel/fixedmovey) rather than
  observed velocity, so a to-be-mover rides the hazard band from tick
  one; the event-spawn path (JE_createNewEventEnemy) also clears the
  latch, which it previously inherited from the slot's prior occupant
  -- that stale latch is why LATER carrier spawns rendered solid while
  the first read translucent.  OTYR_TRACE gains "latch-capability"
  (slot, cell) logging every save the new rule makes.  Turbo hash gate
  bit-identical over 89,009 ticks; demo-sweep classifications unchanged
  (the carrier class does not occur in the five shipped demos, so
  in-headset level-1 play plus the trace is the confirming test).
  Harness: the bg sweep now runs two Stage-B detectors -- static->mover
  promotions (aux 1->0 mid-lifetime with positional continuity; zero in
  demos pre- and post-fix) and moving statics (aux-1 records deviating
  from their band's modal scroll).  The latter fires ~159 times across
  demos 1-2, ALL from enemyground-flagged units (tanks/turrets), which
  classify aux 1 unconditionally and legitimately drive around inside
  the flat terrain lane -- by-design Stage A behavior, noted here so
  the number is not mistaken for a regression.  It also surfaced that
  sheet 7 (Coins&Gems) exports 0 cells while demo records reference it
  (aux 1, so the flat frame covers it today; a 3D coin record would
  render invisible -- Stage B checklist item).
- Headset round 4 (2026-07-12): ship-edge sliver GONE, carrier solid
  from first spawn, explosions/text/tally all held.  PIP SOLVED (the
  in-headset screenshot settled it): pause and the in-game menu run
  MID-TICK, and the menu's first present published the interrupted
  tick's PARTIAL record buffer as a snapshot -- the host rendered that
  fragment set frozen for the whole pause.  The pip is a piece of the
  special-weapon HUD icon (its red core), floating proud at its exact
  in-play position over the flat menu box ("interfering with text").
  An earlier hypothesis blamed the menu help line at (10,147) -- wrong:
  those trace lines are the menu's own flat-by-design drawing, wiped
  from the play region every present.  FIX: only the tick-completing
  present (JE_starShowVGA sets otyr_tick_present) may publish sprite
  records; mid-tick presents keep the last complete tick's records,
  drop the OTYR_CAT_TEXT layer (frozen HUD must not float over the
  menu), and fire no sounds.  Hash gate bit-identical (76,544 ticks);
  harness record verify unchanged (demo presents are all
  tick-completing).  The tripwire stays armed.  NEW (confirmed in code): statics
  riding floating platforms (aux 2, and top-band aux 1 with elevated
  surface) visibly swim/blur against their platform under head motion
  -- BackgroundLayer.OnRender smooth-scrolls the ELEVATED tile layers
  every render frame, while rider quads deliberately step per tick
  (they skip snapshot interpolation so they don't swim against the
  baked underlay -- correct for the GROUND layer, which also steps,
  but wrong for interpolated platforms).  FIXED same round: BackgroundLayer records each elevated layer's
  sub-tick offset (interpolated origin minus current origin) in
  OnRender and exposes SubTickOffsetAt(z); WriteTransforms adds it to
  every elevated-surface decal (DecalOrder > 0, Z above ground), so
  riders and platform move as one -- shadows decaling clouds pick up
  the cloud layer's delta the same way.  True-ground decals are
  unaffected (ground layer and its decals both step), matching the
  report.  Awaiting headset confirmation.

## 1. Product direction

The first VR version should preserve Tyrian's rules, timing, levels, weapons, and
enemy choreography while changing how the playfield is presented and controlled.
It should feel like a miniature arcade world streaming toward the player, not a
flat game stretched across a headset.

The default playfield is a wide, tilted lane extending away from the player:

- level travel advances from the far end toward the player, similar to the visual
  flow of Guitar Hero;
- the player ship and airborne enemies hover above the lane;
- ground enemies sit on or just above the terrain;
- clouds, projectiles, explosions, and pickups occupy intentionally separated
  height bands;
- the ship moves across the lane and along a bounded near/far range without
  changing the original gameplay coordinates or collision rules;
- head movement changes the view, never the gameplay aim or simulation;
- controller, gamepad, and accessibility-friendly seated controls are supported.

This is a **2.5D diorama conversion**, not a first-person conversion. The original
320x200 coordinate system remains the authoritative gameplay plane. Depth is
primarily presentation metadata, so the game can remain mechanically faithful.

Distribution stance: the project is free and open source, funded at most by
voluntary donations. Distribution targets are direct downloads and SideQuest;
no paid storefront and no paywalled content. This stance simplifies the
licensing analysis in section 8.

## 2. Findings from the source

OpenTyrian is a C99/SDL2 software-rendered game. It draws an indexed-color
320x200 frame into `SDL_Surface` buffers and uploads the completed frame in
`JE_showVGA()` (`src/video.c`).

The main gameplay frame in `JE_main()` (`src/tyrian2.c`) interleaves:

1. level event execution;
2. background tile drawing and scrolling;
3. enemy simulation and sprite drawing;
4. projectile simulation, drawing, and collision;
5. player input, simulation, and drawing;
6. explosions, HUD, sound dispatch, timing, and presentation.

Important consequences:

- replacing the renderer is not a single backend swap;
- several functions named as drawing functions also mutate gameplay state;
- draw order currently encodes semantic layers (ground, sky, top enemies);
- gameplay uses global state extensively;
- background maps are already structured data, not merely finished screenshots:
  three tile layers use 24x28 shapes and maps of 14x300, 14x600, and 15x600;
- level events, enemies, player shots, enemy shots, explosions, and audio are
  available as structured state and should be exported rather than recovered
  from pixels;
- menus, cinematics, palette effects, and unusual legacy filters are much easier
  to retain initially as a 2D surface;
- `JE_main()` (`src/tyrian2.c`) is a single ~3,500-line function containing its
  own frame loop, and menus, the title screen, cinematics, and pause each run
  their own nested blocking loops that call `JE_showVGA()`. The only entry point
  is `main()` in `src/opentyr.c`. There is no tick-callable core today; making
  one is Phase 2 work, and earlier phases must host the game as-is (see
  section 6);
- the core is built directly on SDL2 (surfaces, timing, audio, SDL2_net). The
  native library will continue to link SDL2 for the foreseeable future; headless
  operation uses SDL's dummy video driver rather than removing SDL. Phase 5 must
  therefore include an arm64 Android build of SDL2.

### Upstream relationship

This working copy is a clone of `github.com/opentyrian/opentyrian`, whose
maintainer is actively refactoring the exact files this plan splits: recent
upstream commits rewrite config/saves, redesign keyboard/mouse input, and
replace globals with fields/parameters in `tyrian2.c` and `mainint.c`. That
work moves in the same direction as Phase 2 and should be absorbed for free
while possible.

Policy:

- develop on a GitHub fork with `upstream` kept as a remote;
- track upstream during Phase 0;
- freeze on a recorded, pinned upstream commit at the start of Phase 2 —
  replay recordings and state hashes are only meaningful against one exact
  base commit;
- after the freeze, upstream merges are rare, deliberate events that re-run
  the full Phase 0 replay baseline.

Pinned base commit: `1c34d1b` ("Rewrite reading and writing of config and
saves; rewrite animation player").

The Zig + Godot XR foundation is currently an architecture/extraction workspace,
not production code. Its most useful lessons are architectural:

- keep simulation and game-specific payloads owned by the game;
- share only stable platform infrastructure;
- initialize XR even if the native backend fails, so diagnostics remain visible;
- use a small, versioned C ABI with explicit ownership and size checks;
- support flat/headless operation;
- place and recenter content relative to the first valid head pose;
- treat PCVR and Quest as separate validated targets;
- make Quest packaging fail closed and verify native payloads before headset
  testing.

OpenTyrian is C rather than Zig, so the Zig-specific parts (custom libc
targeting, the Zig-side big-stack helper) do not transfer directly — an NDK C
build links bionic natively. But several foundation findings transfer verbatim:

- the APK must ship with `extractNativeLibs=true`, or .NET P/Invoke `dlopen`
  of the native library fails on Quest (foundation ADR 0002);
- Quest is a 16 KB-page OS: `zipalign -P 16`;
- Godot 4.7's Android export template requires the .NET target framework to be
  `net9.0` (it rejects net8.0);
- .NET host threads default to ~1 MiB stacks; any thread the native library
  creates for the game loop should be given a generous explicit stack size;
- ABI structs are pinned by host-side round-trip tests asserting exact struct
  sizes, and the ABI version guard throws on mismatch rather than warning.

## 3. Recommended architecture

### Native game core

Refactor OpenTyrian into a native library while retaining a normal SDL desktop
executable as a regression harness.

The core owns:

- all gameplay rules and fixed-tick simulation;
- level and asset loading;
- collision and random-number state;
- save data and replay/determinism policy;
- music and sound event selection;
- the legacy 320x200 renderer used for menus, cinematics, fallback, and
  comparison.

Expose a small game-owned C ABI:

```c
uint32_t otyr_abi_version(void);
uint64_t otyr_capabilities(void); /* feature flags: snapshots, events, ... */
int32_t  otyr_last_error(char *buffer, uint32_t buffer_size);
int32_t  otyr_session_create(const void *config, uint32_t config_size,
                             uint64_t *out_session);
int32_t  otyr_session_destroy(uint64_t session);
int32_t  otyr_session_tick(uint64_t session, const OtyrInputFrame *input);
int32_t  otyr_session_snapshot(uint64_t session, OtyrSnapshot *snapshot);
int32_t  otyr_session_events(uint64_t session, OtyrEventBuffer *events);
int32_t  otyr_legacy_frame(uint64_t session, OtyrFrameBuffer *frame);
```

All ABI structs should be fixed-width, append-only, versioned, and validated on
both sides. Validation is concrete: host-side round-trip tests assert exact
struct sizes, and a mismatched `otyr_abi_version` is a hard failure at session
create, not a warning. Snapshot buffers should be caller-owned or explicitly
borrowed until the next tick; no ambiguous ownership.

`otyr_session_tick` and `otyr_capabilities` do not exist until Phase 2. Phase 1
and the first spike host the unmodified blocking game loop on a dedicated
thread instead (see section 6); the host detects which mode the library
supports via `otyr_capabilities` once it exists.

### Godot VR host

Use Godot 4 .NET as the XR presentation and platform shell, consistent with the
foundation direction.

Godot owns:

- OpenXR bootstrap, flat fallback, tracked devices, recentering, and diagnostics;
- fixed-tick scheduling and interpolation between snapshots;
- the lane/terrain mesh and all 3D presentation;
- sprite/mesh pools for ships, enemies, shots, pickups, clouds, and explosions;
- VR input mapping into the original abstract input frame;
- spatial audio presentation, HUD, menus, comfort settings, and pause behavior;
- Windows PCVR and Quest packaging.

Keep the OpenTyrian snapshot schema local to this repository. Consume a pinned
foundation release later for XR bootstrap, native loading, diagnostics, and
Quest automation only after that foundation has real implementation and a
compatible C-native path.

### Coordinate mapping

Use one documented transform:

```text
Tyrian x (drawable play area, ~0..264) -> lane local X
Tyrian y (drawable play area, 0..199)  -> lane local Z, reversed so 0 is far away
semantic layer                         -> lane local Y (height)
```

The transform is defined over the drawable play surface so entities can enter
and leave the lane gracefully (enemy shots despawn at roughly x 0..275,
y -14..190). The player ship itself is clamped much tighter — x in [40, 256]
and y in [10, 160] (`src/mainint.c`) — so comfort tuning for the ship's
near/far travel must use that smaller band, not the full surface.

Collisions continue entirely in Tyrian coordinates. Visual heights are assigned
by category and optional asset metadata:

- terrain: 0;
- ground enemies and ground effects: terrain height plus a small offset;
- pickups and player ship: low flight band;
- ordinary air enemies and shots: middle flight band;
- clouds and high enemies: upper flight band;
- HUD: head-locked only for essential status, with most information mounted near
  the playfield.

The lane dimensions, tilt, distance, and height exaggeration are comfort-tunable.

## 4. Terrain and depth strategy

Do not make Gaussian splatting a prerequisite for the first playable build.
The source art is low-resolution, palette-based, tiled, and often animated or
palette-filtered. Unconstrained single-image depth generation can introduce
geometry that changes between adjacent repeated tiles, shimmers in stereo, and
misrepresents collision.

Use a staged asset pipeline:

### Stage A: deterministic 2.5D terrain

- decode each 24x28 background shape and palette to RGBA;
- build the visible map strip from the original tile indices;
- render it on a subdivided lane mesh;
- derive conservative height, normal, and roughness maps using authored rules
  plus optional depth estimation;
- enforce identical edge heights for identical tile edges to prevent seams;
- keep water, lava, stars, and palette effects as shaders or overlay planes;
- cache generated artifacts by source asset hash.

This provides stereo depth, stable motion, and exact texture fidelity.

### Stage B: authored semantic depth

Classify recurring tiles/materials (water, road, crater, wall, foliage, lava,
space) and give them controlled depth profiles. Add hand-authored overrides for
hero levels. This will usually outperform unconstrained AI depth on pixel art.

### Stage C: experimental reconstruction

Evaluate Gaussian splats, neural depth, or image-to-3D offline for selected
background set pieces and sky volumes. Convert or bake results into a performant,
stable representation for Quest where possible. Require stereo comfort,
frame-time, seam, and art-fidelity comparisons against Stage B before adoption.

Gaussian splats are more promising for distant scenery and clouds than for the
collidable scrolling ground. They should never be the authoritative gameplay
surface.

## 5. Delivery phases and gates

### Phase 0 — Baseline and archaeology

Deliver:

- GitHub fork created, `upstream` remote configured, pinned base commit and
  upstream-merge policy recorded (see section 2);
- reproducible Windows desktop build;
- one short representative level capture;
- scripted input recording;
- per-tick hashes of important simulation state;
- captured legacy frame hashes at checkpoints;
- entity/layer taxonomy and coordinate document;
- license and asset-distribution review.

Note that the hash instrumentation itself modifies the game, so the
*instrumented build at the pinned commit* is the baseline. Hashes prove
self-consistency of this fork across refactors, not fidelity to upstream.

Gate: a recorded run replays with matching state hashes.

### Phase 1 — VR shell with the unchanged game surface

Deliver:

- Godot OpenXR project with flat fallback;
- head-relative placement and recenter;
- original framebuffer shown on a tilted, scrolling arcade board;
- gamepad and VR-controller input mapped to original actions;
- original audio;
- PCVR build with stable frame pacing.

This deliberately proves the native boundary, timing, controls, scale, and
comfort before the renderer split.

Gate: complete one level in-headset with mechanics matching desktop.

### Phase 2 — Simulation/presentation seam

Refactor one subsystem at a time:

1. introduce `InputFrame`;
2. isolate exactly one gameplay tick from waiting and presentation;
3. split enemy update from enemy draw;
4. split projectile update/collision from draw;
5. split player update from draw;
6. emit audio and effect events;
7. publish a versioned snapshot;
8. retain the old renderer as a comparison path.

Gate: the same recorded input produces matching state hashes before and after
each extraction.

### Phase 3 — Hybrid 3D vertical slice

Convert one representative level:

- 3D lane using original background tiles;
- terrain depth/normal prototype;
- billboarded player ship, enemies, shots, pickups, and explosions at semantic
  height bands;
- correct layer ordering and parallax;
- legacy HUD rendered to a world-space panel;
- pooled objects and snapshot interpolation.

Gate: desktop legacy and VR hybrid runs remain mechanically identical; PCVR
meets frame-time and comfort targets.

### Phase 4 — Full gameplay presentation

- cover every enemy grouping and special draw-order case;
- port background effects and filters to shaders;
- add cloud and distant-scenery layers;
- replace essential HUD elements with readable spatial UI;
- support story prompts, pause, death, end-level, and two-player presentation;
- preserve menus/cinematics on the legacy surface until individually redesigned.

Gate: campaign smoke test with no missing entity/event categories.

### Phase 5 — Quest and production hardening

- Android arm64 native build via the NDK, including SDL2 for arm64;
- Godot Quest export and OpenXR Vendors integration; export template requires
  the .NET target framework to be `net9.0`;
- pinned toolchain (Godot version, .NET TFM, OpenXR Vendors plugin, JDK,
  Android build tools) and parameterized build script;
- APK payload/manifest verification, alignment, signing, install, and launch
  diagnostics — fail closed: `extractNativeLibs=true` asserted in the
  manifest (P/Invoke `dlopen` fails on Quest without it), `zipalign -P 16`
  (16 KB pages), archive listing must contain the game library and the
  OpenXR loader/vendors libraries;
- GPU/CPU profiling, draw-call reduction, texture atlases, pooling, and quality
  tiers;
- seated/standing presets, dominant-hand options, remapping, reduced motion,
  brightness, and playfield-distance controls;
- hand-steering settings: dead-zone size, and control-rectangle visibility
  (always visible / fade out N seconds after level start / hidden).

Gate: representative levels run on the target Quest headset without missed
simulation ticks or comfort regressions.

### Phase 6 — Optional visual upgrades

- authored 3D ship/enemy models while retaining sprite mode;
- depth-enhanced set pieces and volumetric clouds;
- mixed-reality backdrop;
- spatialized effects and reactive haptics;
- experimental splat-based scenery if it passes performance and stability gates.

## 6. First technical spike

The first spike should be intentionally narrow and disposable only at the Godot
presentation layer. It must NOT attempt to make the game tick-callable — that
is Phase 2. `JE_main()` and every menu/cinematic contain their own blocking
loops, so the spike hosts the game **thread-hosted**:

1. build the C code as a DLL; `otyr_session_create` spawns a dedicated thread
   (with a generous explicit stack size — .NET host threads default to ~1 MiB)
   that runs the unmodified `main()`/`JE_main()` path;
2. run under SDL's dummy video driver — no SDL window, but SDL2 stays linked
   for surfaces, timing, and audio;
3. turn `JE_showVGA()` and the frame-delay functions into the synchronization
   point: each legacy "present" becomes a buffer handoff plus input exchange
   with the host, which paces the game thread. This one interception handles
   gameplay, menus, and cinematics uniformly with near-zero refactoring;
4. expose create/destroy, input-frame submission, legacy-frame retrieval, and
   minimal player-state calls;
5. upload the 320x200 indexed frame to a Godot texture;
6. display it on an adjustable tilted lane in OpenXR;
7. map a controller thumbstick and buttons to move/fire/rear/sidekick;
8. record tick time, render time, and input-to-photon behavior;
9. verify one replay against the desktop executable.

Time-box this spike before extracting full snapshots. Its purpose is to answer
the highest-risk questions: whether the C core can be hosted safely, whether the
fixed tick behaves under Godot pacing, and whether the Guitar Hero presentation
is comfortable and fun. `otyr_session_tick` re-entrancy arrives in Phase 2, and
only for the gameplay loop — menus and cinematics stay thread-hosted on the
legacy surface until individually redesigned.

## 7. Decisions to make after the spike

- preferred control metaphor: **decided — hand-position steering is the goal**
  (thumbstick stays as the implemented fallback and accessibility mode);
- canonical play posture: seated tabletop, standing cabinet, or both;
- ship motion mapping: direct position, velocity, or hybrid;
- whether player-controlled near/far movement changes only screen Y or also
  visual altitude;
- target headset and minimum PCVR runtime;
- whether two-player/network play is in the first release;
- how much legacy UI remains world-space 2D versus being rebuilt;
- whether redistribution can include the freeware Tyrian data or must require
  users to supply it. Partially answered: for free channels (direct download,
  SideQuest) bundling is customary and low-risk — Tyrian 2.1 data has been
  freeware since 2004 and ships with most OpenTyrian ports. The permission is
  informal, not a license, and "Tyrian" remains a trademark, so any storefront
  or paid distribution would change the answer; this project does neither.

## 8. Principal risks

| Risk | Mitigation |
|---|---|
| Simulation and rendering are interleaved | Extract by subsystem with replay/state hashes and keep the legacy renderer |
| VR frame rate differs from legacy tick rate | Fixed simulation tick plus interpolated presentation |
| Added depth makes hazards ambiguous | Preserve 2D collision plane, use strict height bands and shadows |
| Forward lane motion causes discomfort | World stays head-stable; move content, tune tilt/speed/FOV, add comfort presets |
| Pixel art looks inconsistent in stereo | Conservative mesh displacement, nearest sampling, authored depth metadata |
| Quest cannot sustain the scene | Pools, atlases, MultiMesh/batching, quality tiers, profile from the vertical slice |
| Menus and palette effects expand scope | Keep a legacy framebuffer path throughout development |
| Foundation is not implemented yet | Use its contracts as guidance; pin and adopt only proven released components |
| Upstream keeps rewriting the files we split | Fork; track upstream in Phase 0, freeze on a pinned commit at Phase 2; merges re-run the replay baseline |
| GPL/data licensing affects distribution | Whole project ships GPL-2.0 (OpenTyrian is GPL; Godot is MIT, compatible). Fine for direct download and SideQuest. The Meta Quest Store is off the table: GPLv2's no-further-restrictions clause conflicts with store terms (the VLC/App Store precedent) and relicensing consent from all OpenTyrian contributors is unobtainable. Donations (Ko-fi, GitHub Sponsors) are fully compatible with GPL — payment for development time, not for the software |

## 9. Definition of a successful first milestone

The milestone is not “some sprites appear in VR.” It is:

- a reproducible desktop and PCVR build;
- the original game hosted through a versioned native boundary;
- a comfortable, recenterable tilted-lane presentation;
- VR controller and gamepad input;
- one level playable from start to finish;
- deterministic comparison with the SDL build;
- diagnostics visible even when native initialization fails;
- measured evidence that the architecture can progress to structured 3D
  snapshots without rewriting Tyrian's gameplay.
