# Entity, layer, and coordinate taxonomy

Phase 0 deliverable (see [VR_CONVERSION_PLAN.md](VR_CONVERSION_PLAN.md) section 5).
This documents how the gameplay frame is actually structured in the source, and
assigns each category a proposed VR height band for the lane presentation. All
line references are against the pinned fork base (`1c34d1b` + instrumentation).

## 1. Surfaces and screen regions

The game composes three 320x200 8-bit surfaces (`src/video.c:77`):

| Surface | Role |
|---|---|
| `game_screen` | Scratch buffer; the scrolling play area and all entities, redrawn every tick |
| `VGAScreenSeg` | Persistent buffer holding the HUD/dashboard sidebar; play area composited into it at present |
| `VGAScreen2` | Alternate target used only while "smoothies" (post-process filters) are active (`tyrian2.c:1274`) |

Screen regions after compositing:

- **Play area: screen x 0–263 (264 px wide).** `JE_starShowVGA` copies 264
  bytes/row from `game_screen` starting at world x = 24 (`tyrian2.c:88-147`).
- **HUD sidebar: screen x 264–319 (56 px wide)**, drawn directly into
  `VGAScreenSeg` (power bar x 269–276, shield/armor bars x 270/307).
- **World-to-screen X: `screen_x = world_x - 24`.** Entities are simulated and
  drawn in `game_screen` coordinates with a 24 px left margin (world x 24–287).
- **Vertical: sim Y == screen Y, no offset.** Visible rows are ~0–183; the
  message bar / warning text occupy y ≈ 178–183. Explosions are culled outside
  y in (-16, 190) (`varz.c:935`); enemy shots outside roughly x 0..275,
  y -14..190 (`tyrian2.c` enemy-shot cull).
- **Player clamp: x ∈ [40, 256], y ∈ [10, 160]** (`mainint.c:3963-3988`);
  y max 154 in network games. Player start is (100, 180) (`tyrian2.c:746`).

## 2. Background layers, scrolling, and parallax

Three tile layers, 24x28 px shapes (`backgrnd.c`):

| Layer | Map | Scroll speed (level start) | Horizontal parallax | Semantic |
|---|---|---|---|---|
| 1 | `JE_MapType[300][14]` | `backMove = 1` | `mapXOfs = tempW/3` (slowest) | Ground/terrain |
| 2 | `JE_MapType2[600][14]` | `backMove2 = 2` | `mapX2Ofs = 2*tempW/3` | Sky/mid |
| 3 | `JE_MapType3[600][15]` | `backMove3 = 3` | `mapX3Ofs = tempW` (fastest) | Top/foreground (clouds, canopy) |

- Parallax offsets are recomputed from the player's X at the **end** of
  `JE_playerMovement` (`mainint.c:4544-4570`), so backgrounds drawn earlier in
  the same tick use the previous tick's offset. Ratio ≈ 1 : 2 : 3 (back
  slowest). When `background3x1` is set, layer 3 locks to layer 1's offset.
- Vertical scroll state: `mapYPos/mapY2Pos/mapY3Pos` pointers into the mega-map,
  advanced by `backPos/backPos2/backPos3`; wrap clamps `BKwrap1..3[to]`
  (`tyrian2.c:1107-1112`). Layer 3 wraps by 15 columns, layers 1/2 by 14.
- The parallax X offsets exist only to fake depth on a flat screen. **The VR
  snapshot should export raw sim coordinates plus layer/band; real 3D geometry
  replaces `mapXOfs/mapX2Ofs/mapX3Ofs`.**
- `background2over` (0/1/2/3) and `background3over` (0/1/2) select *where in
  the frame* layers 2 and 3 are drawn (under/over entities) — draw order is
  data-driven per level, not fixed.

## 3. Entity taxonomy and proposed VR height bands

Bands per plan section 3: `terrain` (0), `ground` (terrain + offset), `low`
(pickups/player), `mid` (air enemies/shots), `high` (clouds/top enemies),
`hud` (world-space panel).

| Category | Pool / slots | Draw site | Original parallax coupling | VR band |
|---|---|---|---|---|
| Terrain tiles (layer 1) | mega-map | `draw_background_1` (`tyrian2.c:1286`) | `mapXOfs`, `backMove` | terrain (lane mesh) |
| Sky tiles (layer 2) | mega-map | 4 possible sites by `background2over` | `mapX2Ofs`, `backMove2` | mid overlay plane |
| Top tiles (layer 3) | mega-map | 3 possible sites by `background3over` | `mapX3Ofs`, `backMove3` | high overlay plane |
| **Ground enemies A** | `enemy[25..49]` | `JE_drawEnemy(50)` (`tyrian2.c:1352`) | layer 1 (`mapXOfs`, `backMove`) | ground |
| **Ground enemies B** | `enemy[75..99]` | `JE_drawEnemy(100)` (`tyrian2.c:1353`) | layer 1 | ground |
| **Sky enemies** | `enemy[0..24]` | `JE_drawEnemy(25)` (`tyrian2.c:1423` or late `1892`) | layer 2 (`mapX2Ofs`, no backMove) | mid |
| **Top enemies** | `enemy[50..74]` | `JE_drawEnemy(75)` (`tyrian2.c:1440` or late `1882`) | layer 3 (`backMove3`) | high |
| Player ship(s) | `player[2]` | `JE_mainGamePlayerFunctions` (`tyrian2.c:1775`) | n/a (drives parallax) | low |
| Sidekicks/options | `player[i].sidekick[2]` | drawn with player | n/a | low |
| Player shots | `playerShotData[81+1]`, `shotAvail[81]` | `tyrian2.c:1443-1758` | n/a | mid (weapon metadata may override) |
| Enemy shots | `enemyShot[60]`, `enemyShotAvail[60]` | `tyrian2.c:1781-1872` | n/a | mid |
| Explosions | `explosions[200]` (`Explosion`) | `tyrian2.c:1943-1974` | `followPlayer`/`fixedPosition` variants | band of source entity |
| Repeating explosions | `rep_explosions[20]` | `tyrian2.c:1901-1941` (spawner, not drawn) | n/a | n/a (emits explosions) |
| Superpixels (debris) | `superpixels[101]` | `JE_drawSP` (`tyrian2.c:2331`) | n/a | band of source + drift |
| Starfield | `starfield_stars` | `tyrian2.c:1308` | n/a | far backdrop volume |
| Boss bars | `boss_bar[2]` | `draw_boss_bar` (`tyrian2.c:2339`) | n/a | hud |
| In-game overlays (cash, lives, superbombs) | — | `JE_inGameDisplays` (`tyrian2.c:2341`) | n/a | hud |
| HUD sidebar (shield/armor/weapon/power bars) | — | `tyrian2.c:1142-1259` on `VGAScreenSeg` | n/a | hud |

**Naming trap:** Tyrian's "sky" enemies (slots 0–24) are the *middle* depth
layer; "top" enemies (slots 50–74) are the *foreground* — nearest the viewer
in 2D, therefore the **highest** altitude band in VR. Do not map "sky" to the
highest band.

Slot mechanics: `JE_drawEnemy(off)` iterates slots `off-25 .. off-1`
(`tyrian2.c:183`); spawns fill `off .. off+24` (`tyrian2.c:3816`). Launched
sub-enemies target a sibling band: ground-A launches into ground-B, sky into
sky, top into top (`tyrian2.c:571`).

## 4. Tick order (one `level_loop` iteration)

Condensed; full detail lives in the source. **Bold** steps both mutate
simulation state and draw — these are the Phase 2 extraction targets.

1. Wrap clamps, music/level-end bookkeeping (state only)
2. HUD sidebar redraw + shield regen on `VGAScreenSeg` (**both**)
3. **Level event system** — spawns, filters, jumps, boss bars (`tyrian2.c:1265`)
4. Background layer 1 draw + scroll update; starfield (**both**)
5. Layer 2 draw (early variants); lava/water filters
6. **Ground enemies** draw+AI (`JE_drawEnemy(50)`, `(100)`)
7. Layer 3 / layer 2 variants; random sky-enemy spawn
8. **Sky enemies** draw+AI (unless `skyEnemyOverAll` defers them after player)
9. **Top enemies** draw+AI (unless `topEnemyOver` defers them)
10. **Player shots** — move, draw, enemy collision, damage, kills (`tyrian2.c:1443-1758`)
11. Player↔enemy collision (state only)
12. **Player + sidekicks** — input, movement, draw, fire new shots, recompute parallax (`tyrian2.c:1775`)
13. **Enemy shots** — move, home, cull, player collision, draw
14. Late layer-3/top/sky draws (the `*over` flags); explosions; low-armor warning
15. Sound dispatch — flush `soundQueue[0..7]` (state only)
16. Overlays: level timer, game over, superpixels, screen filter, boss bars, in-game displays
17. Present: `JE_starShowVGA` composites play area into `VGAScreenSeg`, frame-pacing wait, `JE_showVGA`
18. Level-end conditions; `goto level_loop`

Ordering subtleties that must survive the Phase 2 split:

- Player shots are drawn **before** the player; the shots on screen were
  spawned at the end of the *previous* tick's player update.
- Parallax offsets used by backgrounds/ground enemies are one tick stale
  (recomputed in step 12, consumed in steps 4–7 of the next tick).
- `skyEnemyOverAll`, `topEnemyOver`, `background2over`, `background3over`
  reorder draw sites per level — the snapshot must carry these (or a resolved
  per-entity sort key), not assume a fixed order.

## 5. Special cases the renderer must represent

- **Hit/ice flash:** `enemy[i].filter` (palette hue) selects a filtered blit for
  one frame after hits (`tyrian2.c:173-176`, cleared at `:251`); icing sets
  `iced=40`, `filter=0x09`, and freezes AI. → per-instance tint parameter.
- **2x2 enemies:** `size==1` enemies are four linked sprite blits
  (`tyrian2.c:232-243`). → one quad with a composite texture.
- **`linknum` chaining:** multi-part bosses share `linknum`; damage, death,
  filter flashes, and `boss_bar` state propagate across the link group
  (`tyrian2.c:1521-1732`). Purely simulation-side, but explains why body parts
  flash together.
- **`enemyground`:** selects ground vs air explosion palettes
  (`varz.c:967`) — also a good ground-vs-air band signal for explosion height.
- **Smoothies** (`smoothies[9]`, CPU-gated): lava/water/iced-blur/blur
  full-screen filters (`backgrnd.c:320+`), plus two present-time effects —
  vertical flip and ship-light (`tyrian2.c:100-142`). → shader ports in
  Phase 4; disabled in network games.
- **Screen filter:** `JE_filterScreen` tints the whole 264x184 play area
  (`backgrnd.c:282-288`) — level events drive it. → color-grade pass.
- **`enemyOnScreen` coupling:** background scrolling stops/restarts and level
  end can trigger based on whether any enemies drew this tick
  (`tyrian2.c:1355-1359`, `2349-2392`). Drawing participates in game logic —
  the Phase 2 split must keep an "entity visible" count in the simulation.
- **Two-player/galaga:** split HUD bars, player-2-as-sidekick in galaga mode,
  enemy aim picks a random live player.

## 6. Audio events

`soundQueue[8]` — one byte per mixer channel, flushed once per tick
(`tyrian2.c:2057-2080`). Channel 3 is reserved for the player weapon (full
volume); enemy fire picks a random channel != 3; sample 15 plays at 1/4
volume, everything else at 1/2. The snapshot event stream should carry
(channel, sample) pairs so VR spatialization can choose emitter positions by
source entity, with channel 3 = player ship.
