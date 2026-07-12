# Stage B hover heights: assignment workflow

`godot/hover_heights.json` maps enemy types (eDat indices) to hover-height
classes. The host applies it to MOVING enemies (statics/riders already sit on
their surfaces via the decal path). Unlisted types keep the legacy category
band, so partial edits are always safe.

## The height editor (preferred workflow)

Launch flat with:

```
OTYR_FLAT=1 OTYR_HEIGHT_EDITOR=1 OTYR_INVULN=1
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
  platform-under / mid-under / over-top ("ground" resolves against the
  surface beneath from the next tick)
- **P** pauses the game (the scene stays up for selection); **N** skips the
  level past progress blockers like end bosses
- **S** saves all pending edits back to hover_heights.json

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
- Edits load at app start (relaunch to apply).

## The first pass (generated 2026-07-12)

`tools/classify_heights.ps1` generated the current file from:
- `captures/edat_dump.csv` — static enemy data (run anything with
  `OTYR_DUMP_EDAT=captures\edat_dump.csv`; appends per episode, currently
  episode 1 only — play a later-episode level with the env set to collect
  more, then re-run the classifier).
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
