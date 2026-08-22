#!/usr/bin/env python3
"""Summarize authored spawn-layer evidence across every episode level."""

from __future__ import annotations

import argparse
import csv
from collections import Counter, defaultdict
from pathlib import Path


SURFACE_EVENTS = {6: 25, 10: 75, 17: 25, 56: 75}
AIR_EVENTS = {15: 0, 18: 0}
TOP_EVENTS = {7: 50, 23: 50, 32: 50}


def spawned_types(row: dict[str, str]) -> list[tuple[int, str]]:
    event_type = int(row["event_type"])
    enemy_type = int(row["d1"])
    if event_type in SURFACE_EVENTS:
        return [(enemy_type, "surface")]
    if event_type in AIR_EVENTS:
        return [(enemy_type, "air")]
    if event_type in TOP_EVENTS:
        return [(enemy_type, "top")]
    if event_type == 12:
        band = {0: "surface", 1: "surface", 2: "air", 3: "top", 4: "surface"}.get(
            int(row["d6"]), "unknown"
        )
        return [(enemy_type + offset, band) for offset in range(4)]
    # Events 49..52 build a temporary enemy definition in slot zero. Their
    # d1 is a graphic id, not a stable episode-local enemy type.
    return []


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--events", type=Path, default=Path("captures/level_events_all_episodes.csv")
    )
    parser.add_argument(
        "--output", type=Path, default=Path("captures/etype_event_observed.csv")
    )
    args = parser.parse_args()

    evidence: dict[tuple[int, int], Counter[str]] = defaultdict(Counter)
    levels: dict[tuple[int, int], set[tuple[int, int]]] = defaultdict(set)
    links: dict[tuple[int, int], set[int]] = defaultdict(set)
    all_levels: set[tuple[int, int, int]] = set()
    raw_events = 0
    custom_events = 0
    with args.events.open(newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            raw_events += 1
            episode = int(row["episode"])
            section = int(row["section"])
            level = int(row["level"])
            all_levels.add((episode, section, level))
            if 49 <= int(row["event_type"]) <= 52:
                custom_events += 1
            for enemy_type, label in spawned_types(row):
                if not 0 <= enemy_type <= 850:
                    continue
                key = episode, enemy_type
                evidence[key][label] += 1
                levels[key].add((section, level))
                if int(row["d4"]):
                    links[key].add(int(row["d4"]))

    fieldnames = [
        "episode", "type", "classification", "surface_spawns", "air_spawns",
        "top_spawns", "unknown_spawns", "levels", "link_ids", "conflict",
    ]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        for key in sorted(evidence):
            counts = evidence[key]
            direct = {label for label in ("surface", "air") if counts[label]}
            classification = next(iter(direct)) if len(direct) == 1 else ""
            writer.writerow({
                "episode": key[0],
                "type": key[1],
                "classification": classification,
                "surface_spawns": counts["surface"],
                "air_spawns": counts["air"],
                "top_spawns": counts["top"],
                "unknown_spawns": counts["unknown"],
                "levels": len(levels[key]),
                "link_ids": ";".join(map(str, sorted(links[key]))),
                "conflict": int(len(direct) > 1),
            })

    classes = Counter()
    for counts in evidence.values():
        direct = {label for label in ("surface", "air") if counts[label]}
        classes[next(iter(direct)) if len(direct) == 1 else "conflict" if direct else "top-only"] += 1
    print(
        f"{raw_events} events across {len(all_levels)} episode/section/level records; "
        f"{len(evidence)} episode-local types observed; {custom_events} dynamic custom spawns excluded"
    )
    print(dict(sorted(classes.items())))
    print(f"wrote {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
