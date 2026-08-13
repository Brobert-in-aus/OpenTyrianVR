# Episode 1 height automation audit

The automatic broad-layer rule is Tyrian's authored enemy-ground bit: a zero low explosion-type bit means surface-attached; one means flying. Ground/building instances then sample the terrain or aerial platform beneath them. Explicit numeric heights remain manual refinements.

The raw ground-bit baseline agrees with **291/371 (78.4%)** binary manual placements recovered from the two saved Episode 1 editing batches. It is useful evidence, but not reliable enough by itself.

| Manual | Auto surface | Auto air |
|---|---:|---:|
| Surface | 119 | 37 |
| Air | 43 | 172 |

The family-aware classifier uses only references from the same sprite bank and auto-applies only at distance <= 10. In leave-one-out testing it covers **260/371** manual placements and agrees on **260/260 (100.0%)**. More distant types are sent to review rather than guessed.

Across the full 851-type Episode 1 table this retains 371 manual references, proposes 13 additional surface and 39 additional air types, and leaves 428 review-only.
68 trusted references sit in mixed-label near families and are retained as overrides but quarantined from family propagation.

Manual samples by save:

- level 1, asteroids, SAVARA start: 69
- later Episode 1 editor session: 63
- verified Episode 1 through WINDY: 356

Disagreements requiring a retained override:

- Type 15: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 20: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 23: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 66: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 67: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 68: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 69: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 70: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 71: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 72: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 73: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 74: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 75: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 76: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 77: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 78: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 79: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 166: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 167: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 228: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 229: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 230: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 231: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 232: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 233: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 234: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 235: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 237: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 238: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 239: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 240: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 241: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 242: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 243: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 244: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 394: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 395: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 396: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 400: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 401: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 402: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 403: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 404: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 405: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 406: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 407: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 408: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 409: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 410: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 411: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 440: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 441: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 442: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 443: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 450: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 451: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 452: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 453: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 459: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 464: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 465: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 466: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 467: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 475: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 546: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 547: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 548: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 549: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 552: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 553: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 555: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 568: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 569: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 570: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 571: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 572: manual `air`, automatic `surface` (verified Episode 1 through WINDY)
- Type 577: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 590: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 593: manual `surface`, automatic `air` (verified Episode 1 through WINDY)
- Type 824: manual `surface`, automatic `air` (verified Episode 1 through WINDY)

High-confidence family-classifier misses:

- None

Stacked-component policy:

- A nonzero native link number defines an assembly for the current tick.
- If any component is authored as ground, the whole assembly uses one topmost sampled surface, preventing tank bodies and turrets from splitting.
- Low explicit component heights are treated as offsets from that shared surface, preserving the hand-authored stack. High explicit heights remain flying exceptions.
- Unlinked flying enemies remain at their authored/default air height.

The audit is intentionally non-destructive; it does not rewrite `godot/hover_heights.json`.
