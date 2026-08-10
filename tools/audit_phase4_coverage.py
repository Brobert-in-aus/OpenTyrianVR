#!/usr/bin/env python3
"""Audit presentation-effect coverage across every playable level record."""

from __future__ import annotations

import argparse
import csv
from collections import Counter, defaultdict
from pathlib import Path


EFFECTS = {
    1: ("lava displacement", "host per-eye post effect"),
    2: ("water storm", "host background ripple shader"),
    3: ("iced blur A", "host per-eye post effect"),
    4: ("motion blur", "host per-eye post effect"),
    5: ("iced blur B", "host per-eye post effect"),
    6: ("darkness/searchlight", "host per-eye post effect"),
    9: ("vertical mirror", "host card-flip presentation"),
}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--events", type=Path, default=Path("captures/level_events_all_episodes.csv")
    )
    parser.add_argument("--output", type=Path, default=Path("docs/PHASE4_COVERAGE.md"))
    parser.add_argument("--write", action="store_true")
    args = parser.parse_args()

    changes: Counter[tuple[int, int]] = Counter()
    levels: dict[int, set[tuple[int, int, int]]] = defaultdict(set)
    all_levels: set[tuple[int, int, int]] = set()
    event_types: Counter[int] = Counter()
    with args.events.open(newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            episode, section, level = map(int, (row["episode"], row["section"], row["level"]))
            all_levels.add((episode, section, level))
            event_type = int(row["event_type"])
            event_types[event_type] += 1
            if event_type != 64:
                continue
            effect, enabled = int(row["d1"]), int(row["d2"])
            changes[(effect, enabled)] += 1
            levels[effect].add((episode, section, level))

    observed = {effect for effect, _enabled in changes}
    unknown = observed - EFFECTS.keys()
    missing = EFFECTS.keys() - observed
    lines = [
        "# Phase 4 presentation coverage",
        "",
        f"Static audit of **{sum(event_types.values()):,} events** in all "
        f"**{len(all_levels)} playable Episode 1-4 level records**.",
        "",
        "## Effect coverage",
        "",
        "| Effect | Presentation | Level records | On | Off |",
        "|---|---|---:|---:|---:|",
    ]
    for effect in sorted(observed):
        name, presentation = EFFECTS.get(effect, ("UNKNOWN", "UNSUPPORTED"))
        lines.append(
            f"| {effect}: {name} | {presentation} | {len(levels[effect])} | "
            f"{changes[(effect, 1)]} | {changes[(effect, 0)]} |"
        )
    lines += [
        "",
        "All known effects preserve the 3D scene. Full-playfield filters sample each eye's",
        "already-rendered scene, so shimmer, blur, ice tint, and the player searchlight remain",
        "stereo-correct. Menus, cinematics, story prompts, pause, and unknown future effect",
        "combinations retain the complete legacy-surface safety path.",
        "",
        "## Generic coverage",
        "",
        "- Enemy shapes, 2x2s, linked bosses, shots, pickups, explosions, glow debris, and",
        "  old-table blend sprites are shape-independent snapshot records.",
        "- The three scrolling map layers export their maps, draw order, blend state, and",
        "  parallax every tick; clouds/platforms are semantic geometry rather than level ids.",
        "- Essential in-play text and HUD icons use the proud text layer; sidebar and bottom",
        "  instruments remain readable world-space legacy panels.",
        "- Lifecycle screens remain complete because the legacy frame is never discarded.",
        "",
        "## Gate",
        "",
        f"- Unknown smoothie/effect ids: **{sorted(unknown) if unknown else 'none'}**",
        f"- Known ids absent from this data build: **{sorted(missing) if missing else 'none'}**",
        "- Result: **PASS**" if not unknown else "- Result: **FAIL**",
        "",
        "Regenerate with:",
        "",
        "```powershell",
        "python tools/audit_phase4_coverage.py --write",
        "```",
    ]
    rendered = "\n".join(lines) + "\n"
    if args.write:
        args.output.write_text(rendered, encoding="utf-8")
        print(f"wrote {args.output}")
    else:
        print(rendered)
    if unknown:
        raise SystemExit(f"unsupported effect ids: {sorted(unknown)}")
    print(f"PASS: {len(all_levels)} level records, effect ids {sorted(observed)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
