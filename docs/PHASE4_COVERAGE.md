# Phase 4 presentation coverage

Static audit of **53,338 events** in all **62 playable Episode 1-4 level records**.

## Effect coverage

| Effect | Presentation | Level records | On | Off |
|---|---|---:|---:|---:|
| 1: lava displacement | host per-eye post effect | 5 | 14 | 2 |
| 2: water storm | host background ripple shader | 8 | 8 | 2 |
| 3: iced blur A | host per-eye post effect | 1 | 1 | 0 |
| 4: motion blur | host per-eye post effect | 4 | 5 | 3 |
| 5: iced blur B | host per-eye post effect | 1 | 1 | 1 |
| 6: darkness/searchlight | host per-eye post effect | 5 | 5 | 2 |
| 9: vertical mirror | host card-flip presentation | 2 | 2 | 2 |

All known effects preserve the 3D scene. Color filters sample each eye's rendered
scene; the player searchlight is a late per-eye alpha mask so transparent terrain
cannot disappear from its input. Menus, cinematics, story prompts, pause, and unknown future effect
combinations retain the complete legacy-surface safety path.

## Generic coverage

- Enemy shapes, 2x2s, linked bosses, shots, pickups, explosions, glow debris, and
  old-table blend sprites are shape-independent snapshot records.
- The three scrolling map layers export their maps, draw order, blend state, and
  parallax every tick; clouds/platforms are semantic geometry rather than level ids.
- Essential in-play text and HUD icons use the proud text layer; sidebar and bottom
  instruments remain readable world-space legacy panels.
- Lifecycle screens remain complete because the legacy frame is never discarded.

## Gate

- Unknown smoothie/effect ids: **none**
- Known ids absent from this data build: **none**
- Result: **PASS**

Regenerate with:

```powershell
python tools/audit_phase4_coverage.py --write
```
