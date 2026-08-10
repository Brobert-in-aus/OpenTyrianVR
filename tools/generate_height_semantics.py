#!/usr/bin/env python3
"""Generate conservative episode-local surface/air placement metadata."""

from __future__ import annotations

import argparse
import csv
import json
from collections import Counter, defaultdict
from pathlib import Path

from analyze_height_semantics import (
    MANUAL_SAVES,
    family_distance,
    git_json,
    manual_semantic,
    same_family,
)
from sweep_height_events import spawned_types


def load_rows(path: Path) -> dict[tuple[int, int], dict]:
    rows: dict[tuple[int, int], dict] = {}
    with path.open(newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            # Raw multi-run captures contain a repeated header per episode.
            if row.get("episode") in (None, "episode"):
                continue
            rows[(int(row["episode"]), int(row["type"]))] = row
    return rows


def manual_references() -> dict[int, tuple[str, str]]:
    labels: dict[int, tuple[str, str]] = {}
    for before_rev, after_rev, name in MANUAL_SAVES:
        before = git_json(before_rev)["types"]
        after = git_json(after_rev)["types"]
        for key in set(before) | set(after):
            if before.get(key) == after.get(key):
                continue
            semantic = manual_semantic(after.get(key, {}))
            if semantic is not None:
                labels[int(key)] = (semantic, name)
    return labels


def linked_spawn_groups(path: Path) -> dict[int, list[set[int]]]:
    """Types spawned together at one tick under one nonzero assembly link."""
    raw: dict[tuple[int, int, int, int, int], set[int]] = defaultdict(set)
    with path.open(newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            link = int(row["d4"])
            if not link:
                continue
            types = {enemy_type for enemy_type, _label in spawned_types(row)}
            if not types:
                continue
            key = (
                int(row["episode"]), int(row["section"]), int(row["level"]),
                int(row["time"]), link,
            )
            raw[key].update(types)
    groups: dict[int, list[set[int]]] = defaultdict(list)
    for (episode, _section, _level, _time, _link), types in raw.items():
        if len(types) > 1:
            groups[episode].append(types)
    return groups


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--edat", type=Path, default=Path("captures/edat_all_episodes.csv"))
    parser.add_argument(
        "--events", type=Path, default=Path("captures/level_events_all_episodes.csv")
    )
    parser.add_argument("--output", type=Path, default=Path("godot/height_semantics.json"))
    parser.add_argument("--write", action="store_true")
    args = parser.parse_args()

    rows = load_rows(args.edat)
    episodes = sorted({episode for episode, _type in rows})
    if episodes != [1, 2, 3, 4]:
        raise SystemExit(f"expected data episodes 1..4, found {episodes}")

    manual = manual_references()
    generated: dict[int, dict[int, dict]] = {episode: {} for episode in episodes}
    for enemy_type, (semantic, source) in manual.items():
        generated[1][enemy_type] = {
            "class": semantic,
            "source": "manual",
            "detail": source,
        }

    # Validate and propagate only from the original manual references. Do not
    # let a generated result become a new seed and form an unbounded chain.
    for enemy_type in range(851):
        if enemy_type in manual:
            continue
        row = rows[(1, enemy_type)]
        references = [
            other for other in manual
            if same_family(row, rows[(1, other)])
        ]
        if not references:
            continue
        nearest = min(references, key=lambda other: family_distance(row, rows[(1, other)]))
        distance = family_distance(row, rows[(1, nearest)])
        if distance <= 10:
            generated[1][enemy_type] = {
                "class": manual[nearest][0],
                "source": "episode-family",
                "reference": nearest,
                "distance": round(distance, 3),
            }

    signature_fields = [
        key for key in rows[(1, 0)] if key not in {"episode", "type"}
    ]

    def signature(row: dict) -> tuple[str, ...]:
        return tuple(row[field] for field in signature_fields)

    signature_refs: dict[tuple[str, ...], list[tuple[int, str]]] = {}
    for enemy_type, entry in generated[1].items():
        signature_refs.setdefault(signature(rows[(1, enemy_type)]), []).append(
            (enemy_type, entry["class"])
        )

    for episode in (2, 3, 4):
        exact: dict[int, dict] = {}
        seedable_exact: set[int] = set()
        for enemy_type in range(851):
            matches = signature_refs.get(signature(rows[(episode, enemy_type)]), [])
            labels = {label for _ref, label in matches}
            if len(labels) != 1:
                continue
            reference = min(ref for ref, _label in matches)
            exact[enemy_type] = {
                "class": next(iter(labels)),
                "source": "episode1-exact",
                "reference": reference,
            }
            if any(
                generated[1][ref]["source"] == "manual"
                for ref, label in matches if label == next(iter(labels))
            ):
                seedable_exact.add(enemy_type)
        generated[episode].update(exact)

        # One local family hop from exact cross-episode matches. Generated
        # family matches never become seeds for further propagation.
        for enemy_type in range(851):
            if enemy_type in exact:
                continue
            row = rows[(episode, enemy_type)]
            references = [
                other for other in seedable_exact
                if same_family(row, rows[(episode, other)])
            ]
            if not references:
                continue
            nearest = min(
                references,
                key=lambda other: family_distance(row, rows[(episode, other)]),
            )
            distance = family_distance(row, rows[(episode, nearest)])
            if distance <= 10:
                generated[episode][enemy_type] = {
                    "class": exact[nearest]["class"],
                    "source": "episode-family",
                    "reference": nearest,
                    "distance": round(distance, 3),
                }

    # Same-tick, same-link spawns are authored assemblies (boss bodies,
    # turrets, and other stacked components). Seed from the already validated
    # map once, require every seeded occurrence to agree, and never let these
    # additions seed another group or a cross-episode transfer.
    groups = linked_spawn_groups(args.events)
    for episode in episodes:
        seeds = dict(generated[episode])
        votes: dict[int, set[str]] = defaultdict(set)
        contradicted: set[int] = set()
        for group in groups[episode]:
            labels = {
                seeds[enemy_type]["class"] for enemy_type in group
                if enemy_type in seeds
            }
            unknown = group - seeds.keys()
            if len(labels) == 1:
                label = next(iter(labels))
                for enemy_type in unknown:
                    votes[enemy_type].add(label)
            elif len(labels) > 1:
                contradicted.update(unknown)
        for enemy_type, labels in votes.items():
            if enemy_type in contradicted or len(labels) != 1:
                continue
            generated[episode][enemy_type] = {
                "class": next(iter(labels)),
                "source": "linked-assembly",
            }

    counts = {
        str(episode): {
            **Counter(entry["class"] for entry in generated[episode].values()),
            "classified": len(generated[episode]),
            "review": 851 - len(generated[episode]),
        }
        for episode in episodes
    }
    output = {
        "version": 1,
        "policy": {
            "family_distance_max": 10,
            "type_delta_max": 16,
            "graphic_delta_max": 8,
            "generated_results_are_not_recursive_seeds": True,
            "linked_assembly_requires_same_tick_and_nonzero_link": True,
        },
        "counts": counts,
        "episodes": {
            str(episode): {
                str(enemy_type): entry
                for enemy_type, entry in sorted(generated[episode].items())
            }
            for episode in episodes
        },
    }
    rendered = json.dumps(output, indent=2, sort_keys=True) + "\n"
    if args.write:
        args.output.write_text(rendered, encoding="utf-8")
        print(f"wrote {args.output}")
    else:
        print(rendered)
    for episode in episodes:
        print(f"episode {episode}: {counts[str(episode)]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
