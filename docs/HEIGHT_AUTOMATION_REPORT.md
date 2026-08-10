# Episode-aware height automation audit

The broad classifier separates `surface` objects (ground or the aerial
platform actually beneath an instance) from `air` objects. Fine height within
those groups remains in `godot/hover_heights.json`.

## Result

Two saved Episode 1 editor batches provide 129 binary manual references:

| Classifier | Covered | Correct | Result |
|---|---:|---:|---:|
| Tyrian ground/explosion bit alone | 129 | 98 | 76.0% |
| High-confidence art family, leave-one-out | 101 | 101 | 100.0% |

The ground bit is evidence, not a decision by itself. Tyrian assigns the
ground explosion palette to several flying multi-part families. The reliable
automation therefore requires a reference in the same sprite bank, close
graphic/type identity, and matching movement/size metadata. A family distance
above 10 is left for review instead of guessed; candidates must also be within
16 type ids or 8 graphic ids of their reference. This conservative threshold
covered 78% of the manual sample with no disagreement in leave-one-out testing.
Across the complete 851-type Episode 1 table, it retains 129 manual references
and confidently proposes another 24 surface and 30 air types before the level
event sweep described below.

## Episodes 2-4 and unedited levels

Enemy type ids are episode-local. ABI v26 exports the active episode with each
snapshot, and `godot/height_semantics.json` keeps separate type maps. The
cross-episode pass accepts only a complete static-data signature match to a
validated Episode 1 placement. It may then make one non-recursive local family
hop under the same strict threshold:

| Episode | Manual | Exact E1 match | Local family | Linked assembly | Classified | Review |
|---|---:|---:|---:|---:|---:|---:|
| 1 | 129 | - | 54 | 22 | 205 | 646 |
| 2 | - | 183 | 0 | 0 | 183 | 668 |
| 3 | - | 183 | 0 | 14 | 197 | 654 |
| 4 | - | 89 | 5 | 0 | 94 | 757 |

Episodes 2 and 3 have identical static enemy tables to Episode 1. Episode 4
changes 378 of 851 same-index definitions, so only exact/family-supported
placements transfer. Episode 5 is not present in this Tyrian data build.

## Complete level-event sweep

The event scanner reads every event in all 62 playable episode/section/level
records, including secret and conditionally selected levels: 53,338 events in
total. It observes 1,424 stable episode-local enemy types. A further 1,775
custom spawns construct temporary type-zero definitions from graphic ids and
are deliberately excluded from type automation.

Tyrian's event names are not semantic height labels. `Ground Enemy`, `Sky
Enemy`, and `Top Enemy` select legacy scrolling/draw slots. Treating the first
two names as surface/air classifications agrees with only 69 of 97 covered
manual Episode 1 placements (71.1%). The generated map therefore does not use
that tempting but unsafe rule.

One event relationship is reliable: multiple types spawned on the same tick
with the same nonzero link id are components of one authored assembly.
Leave-one-out comparison covers 88 manual placements and agrees on all 88.
The generator uses this relationship for one non-recursive pass, only where
all existing seeds agree; reused, contradictory, single-component, and
unseeded links remain review-only. This adds 22 Episode 1 and 14 Episode 3
component types without turning event-channel names into guessed heights.

Run the reproducible, non-destructive audit with:

```powershell
python tools/analyze_height_semantics.py
python tools/analyze_height_semantics.py --csv artifacts/height-semantics.csv
python tools/sweep_height_events.py
python tools/generate_height_semantics.py --write
```

## Runtime placement

- A surface-class instance samples the terrain or aerial-platform art beneath
  it. The type says what an object is; the current map position says which
  surface it belongs to.
- Explicit numeric heights remain manual refinements and exceptions. Low
  explicit heights on grounded assemblies are offsets from the shared surface;
  high explicit heights stay absolute flying placements.
- A nonzero native link number identifies a stacked assembly for the tick. If
  it is grounded, all components use one topmost sampled surface, while their
  individual offsets preserve the stack. This prevents tank-boss bodies and
  turrets from splitting when different components overlap different map art.
- Clouds are geometrically capped below the platform plane and render before
  platform material. Level 1's cloud layer therefore cannot cross its aerial
  platform layer.

The generator does not rewrite the hand-authored Episode 1 fine-height table.
Low-confidence and contradictory families remain unclassified for manual
review and fall back to their runtime draw band.
