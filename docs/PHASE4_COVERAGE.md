# Phase 4 presentation coverage

Static audit of **53,338 events** in all **62 playable Episode 1-4 level records**.

## Effect coverage

| Effect | Presentation | Level records | On | Off |
|---|---|---:|---:|---:|
| 1: lava displacement | legacy full-frame fallback | 5 | 14 | 2 |
| 2: water storm | host background ripple shader | 8 | 8 | 2 |
| 3: iced blur A | legacy full-frame fallback | 1 | 1 | 0 |
| 4: motion blur | legacy full-frame fallback | 4 | 5 | 3 |
| 5: iced blur B | legacy full-frame fallback | 1 | 1 | 1 |
| 6: darkness/searchlight | legacy full-frame fallback | 5 | 5 | 2 |
| 9: vertical mirror | host card-flip presentation | 2 | 2 | 2 |

The host-native effects preserve the 3D scene. Filters that read and rewrite the
already-composited 320x200 frame deliberately switch the whole tick to the complete
legacy surface; entities, HUD, and backgrounds cannot go missing or be double-drawn.
Menus, cinematics, story prompts, pause, and unsupported full-frame filters use the
same retained legacy-surface path by design.

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

The runtime half of this gate is `tools/test_presentation.ps1`. It performs
silent, self-terminating hybrid/card-flip, native-storm, and legacy-fallback
runs and validates addressed captures plus presentation/shadow telemetry.
