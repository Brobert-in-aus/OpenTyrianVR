# Stage B hover heights: assignment workflow

`godot/hover_heights.json` maps enemy types (eDat indices) to hover-height
classes. The host applies it to MOVING enemies (statics/riders already sit on
their surfaces via the decal path). Unlisted types keep the legacy category
band, so partial edits are always safe.

Broad semantic automation and its Episode 1 comparison are documented in
[`HEIGHT_AUTOMATION_REPORT.md`](HEIGHT_AUTOMATION_REPORT.md). The classifier is
intentionally conservative: it propagates a manual surface/air decision only
within a close sprite family and sends distant matches to review. At runtime,
surface-class instances choose ground versus floating platform from the map art
beneath them; linked boss components share that surface decision.
`godot/height_semantics.json` is the generated episode-aware broad-layer map;
`hover_heights.json` remains the hand-authored Episode 1 fine-height map.
Temporary type-zero enemies use their retained base graphic as a semantic-only
key. They are auto-placed only when event use and validated stable reuse of the
same art unanimously agree, and are intentionally not exposed as editable type
zero entries.

## The height editor (preferred workflow)

Launch via the wrapper (level select included):

```
tools\editor.ps1                  # title screen
tools\editor.ps1 -Section 6       # boot straight into ASTEROID1
tools\editor.ps1 -Section 42      # secret levels have sections too
tools\editor.ps1 -ListSections    # print the section table
```

Episode 1: 4 TYRIAN, 6 ASTEROID1, 7 ASTEROID2, 11 SAVARA, 14 MINES,
17 BUBBLES, 20 DELIANI, 22 ASTEROID?, 24 MINEMAZE, 26 BONUS, 29 HOLES,
30 SAVARA, 32 SOH JIN, 34 WINDY, 37 ASSASSIN, 39 SAVARA V, 42 ** ALE **.

Raw envs, if launching manually:

```
OTYR_FLAT=1 OTYR_HEIGHT_EDITOR=1 OTYR_INVULN=1
[OTYR_START_SECTION=<n> OTYR_START_EPISODE=<e>]
Godot_..._console.exe --path <repo>\godot --xr-mode off
```

The camera leans steeply so heights read at a glance. Navigate the menus
(Enter/Esc/Space) into a game — the ship parks invulnerable and never moves
or fires while the level plays itself. Then:

- **Click** an enemy to select its TYPE (the label shows type id + height;
  every live instance of the type moves together)
- **Up/Down** nudge height ±0.002 (**Shift** = ±0.01), visible immediately,
  even while paused
- **1–8** assign classes: ground / pickup / air-low / air-mid / air-high /
  platform-under / platform / mid-under / over-top ("ground" resolves against the
  surface beneath from the next tick)
- **P** pauses the game (the scene stays up for selection); **N** skips the
  level past progress blockers like end bosses
- **[** rewinds one second and **]** moves one second forward; hold **Shift**
  for single-simulation-tick steps. The editor pauses automatically while the
  retained frame is displayed, so a rapid object can be selected and edited.
  **Backspace** returns to live play.
- **S** saves all pending edits back to hover_heights.json

The timeline retains up to 30 seconds of complete native presentation
snapshots and their palettes. It resets at level boundaries and sprite-bank
changes because the native asset API exposes only the current map/atlas epoch.
Rewind is editor-only and does not alter the normal or Quest simulation path.

Entries carrying a `review` key are unresolved propagation cases. Every live
instance gets a pulsing green editor marker and the selection label shows
`[REVIEW]`; red hazard markers are suppressed for those types so the colors do
not combine misleadingly. Assigning and saving a class or explicit height
removes both `review` and `auto` from that entry.

Both `OTYR_HEIGHT_EDITOR` and `OTYR_INVULN` mutate behavior and must never be
set for normal sessions (the hash gate only holds without them).

## File format

```json
{
  "classes": { "ground": 0.004, "pickup": 0.040, "air-low": 0.045,
               "air-mid": 0.055, "air-high": 0.070 },
  "types": {
    "2":  { "class": "ground", "seen": "demo sheet=5 index=169 ticks=..." },
    "17": { "class": "air-mid" },
    "40": { "height": 0.062 }
  }
}
```

- `ground` is an offset ABOVE the surface beneath the enemy (terrain or
  platform, resolved per tick) — tanks crossing a platform climb with it.
- The air classes are absolute lane heights (player flies at 0.040).
- An explicit `height` overrides any class.
- `review` is an editor-only triage note; it does not change presentation
  height by itself.
- `auto` records propagation provenance and is removed with `review` after a
  manual assignment is saved.
- Edits load at app start (relaunch to apply).

## The first pass (generated 2026-07-12)

`tools/classify_heights.ps1` generated the current file from:
- `captures/edat_dump.csv` — the historical Episode 1 static enemy data.
- `captures/edat_all_episodes.csv` — normalized Episodes 1-4 static data used
  by `tools/generate_height_semantics.py`. Episode 5 is absent from this data
  build. Set `OTYR_DUMP_EDAT=<path>` to refresh raw episode captures.
- `captures/level_events_all_episodes.csv` — all 53,338 raw events in the 62
  playable Episode 1-4 section/level records. Regenerate it with
  `OTYR_DUMP_EVENT_CSV=<path>` and `OTYR_DUMP_SECTIONS=1`.
- `captures/etype_event_observed.csv` — per-episode type usage summary from
  `tools/sweep_height_events.py`; legacy ground/sky/top channels are evidence,
  not assumed 3D heights.
- `captures/etype_observed.csv` — demo observations (harness with
  `OTYR_BG_SWEEP=300`): which types actually appear, their band/aux/motion.

Rules used: legacy ground flag → `ground`; indestructible score items →
`pickup`; player-seekers → `air-low`; 2x2 flyers → `air-high`; rest →
`air-mid`. Crude by design — the manual pass is the authority.

## Manual pass

1. The `seen` note gives sheet and cell: find the art in `captures/sheetN.bmp`
   (32 cells per row, 12x14 each) to identify the enemy visually.
2. Demo-observed types (112 of 851) are the ones that matter first — sort by
   `ticks` in `etype_observed.csv` for screen-time priority.
3. Judge in-headset with the type visible, tweak the class or give an exact
   `height`, relaunch.
4. Types you never see can stay on the generated guess.

## Episode-aware generated pass

`tools/generate_height_semantics.py --write` regenerates
`godot/height_semantics.json`. It never treats an episode-local type id as a
global identity: E2-E4 transfers require a complete static-data signature
match, followed by at most one close local-family hop. Generated results do not
seed another hop. One non-recursive same-tick/same-link assembly pass then
classifies only components whose existing seeds all agree. Unclassified and
conflicting types retain their runtime category band. Two validated event
intersections extend stable coverage (top-only and statically corroborated
air), while dynamic graphics require unanimous event and exact-art agreement.
