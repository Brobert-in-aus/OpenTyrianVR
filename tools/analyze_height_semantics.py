#!/usr/bin/env python3
"""Audit Tyrian's authored ground bit against saved Episode 1 VR heights.

This is deliberately report-only.  Runtime placement uses the same signal,
but this tool never rewrites hover_heights.json or erases hand tuning.
"""

from __future__ import annotations

import argparse
import csv
import json
import subprocess
from collections import Counter
from pathlib import Path

from sweep_height_events import spawned_types


MANUAL_SAVES = (
    ("9a69241^", "9a69241", "level 1, asteroids, SAVARA start"),
    ("2ab9a7d", "98ca9a3", "later Episode 1 editor session"),
)

# User-verified Episode 1 levels, inclusive through WINDY. These are script
# section ids rather than level ordinals; secret/branch levels have their own
# sections between the main path entries.
VERIFIED_EPISODE1_SECTIONS = {
    4, 6, 7, 11, 14, 17, 20, 22, 24, 26, 29, 30, 32, 34,
}


def git_json(revision: str) -> dict:
    raw = subprocess.check_output(
        ["git", "show", f"{revision}:godot/hover_heights.json"], text=True
    )
    return json.loads(raw)


def manual_semantic(entry: dict) -> str | None:
    cls = entry.get("class")
    if cls in {"ground", "platform", "platform-under"}:
        return "surface"
    if cls in {"mid-under", "pickup", "air-low", "air-mid", "air-high", "over-top"}:
        return "air"
    height = entry.get("height")
    if isinstance(height, (int, float)):
        if height < 0.015:
            return "surface"
        if height >= 0.028:
            return "air"
    return None


def completed_level_references(events_path: Path, hover_path: Path) -> dict[int, tuple[str, str]]:
    """Trusted labels from user-verified Episode 1 levels through WINDY.

    The current height table is authoritative for every stable type spawned in
    these levels; generated descendants remain non-recursive seeds in callers.
    """
    hover = json.loads(hover_path.read_text(encoding="utf-8"))["types"]
    level_types: set[int] = set()
    with events_path.open(newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            if (int(row["episode"]) != 1 or
                    int(row["section"]) not in VERIFIED_EPISODE1_SECTIONS):
                continue
            level_types.update(
                enemy_type for enemy_type, _label in spawned_types(row)
                if 0 <= enemy_type <= 850
            )
    trusted: dict[int, tuple[str, str]] = {}
    for enemy_type in level_types:
        semantic = manual_semantic(hover.get(str(enemy_type), {}))
        if semantic is not None:
            trusted[enemy_type] = (semantic, "verified Episode 1 through WINDY")
    return trusted


MOTION_FIELDS = (
    "xmove", "ymove", "xaccel", "yaccel", "xcaccel", "ycaccel",
    "xrev", "yrev", "animate",
)


def family_distance(a: dict, b: dict) -> float:
    """Distance within one sprite bank; <=10 is the auto-apply boundary."""
    return (
        abs(int(a["type"]) - int(b["type"])) * 0.01
        + abs(int(a["egraphic0"]) - int(b["egraphic0"])) * 0.05
        + (a["ground"] != b["ground"]) * 10
        + (a["size"] != b["size"]) * 10
        + sum(a[field] != b[field] for field in MOTION_FIELDS) * 15
    )


def same_family(a: dict, b: dict) -> bool:
    return (
        a["shapebank"] == b["shapebank"]
        and (
            abs(int(a["type"]) - int(b["type"])) <= 16
            or abs(int(a["egraphic0"]) - int(b["egraphic0"])) <= 8
        )
    )


def conflicted_family_references(
    labels: dict[int, tuple[str, str]], rows: dict[int, dict]
) -> set[int]:
    """References too close to an opposite label to seed family inference."""
    conflicted: set[int] = set()
    types = sorted(labels)
    for index, a in enumerate(types):
        for b in types[index + 1:]:
            if labels[a][0] == labels[b][0]:
                continue
            if same_family(rows[a], rows[b]) and family_distance(rows[a], rows[b]) <= 10:
                conflicted.update((a, b))
    return conflicted


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--edat", type=Path, default=Path("captures/edat_dump.csv"))
    parser.add_argument("--events", type=Path, default=Path("captures/level_events_all_episodes.csv"))
    parser.add_argument("--hover", type=Path, default=Path("godot/hover_heights.json"))
    parser.add_argument("--report", type=Path)
    parser.add_argument("--csv", type=Path)
    args = parser.parse_args()

    with args.edat.open(newline="", encoding="utf-8") as handle:
        rows = [r for r in csv.DictReader(handle) if r["episode"] == "1"]
    by_type = {int(r["type"]): r for r in rows}

    labels: dict[int, tuple[str, str]] = {}
    batch_counts: list[tuple[str, int]] = []
    for before_rev, after_rev, name in MANUAL_SAVES:
        before = git_json(before_rev)["types"]
        after = git_json(after_rev)["types"]
        changed = sorted(set(before) | set(after), key=int)
        accepted = 0
        for key in changed:
            if before.get(key) == after.get(key):
                continue
            semantic = manual_semantic(after.get(key, {}))
            if semantic is not None:
                labels[int(key)] = (semantic, name)
                accepted += 1
        batch_counts.append((name, accepted))
    completed = completed_level_references(args.events, args.hover)
    labels.update(completed)
    batch_counts.append(("verified Episode 1 through WINDY", len(completed)))

    confusion: Counter[tuple[str, str]] = Counter()
    disagreements: list[tuple[int, str, str, str]] = []
    for enemy_type, (manual, source) in sorted(labels.items()):
        row = by_type.get(enemy_type)
        if row is None:
            continue
        # The dump column is the low explosion-type bit.  Zero is Tyrian's
        # enemyground=true; its historical column name is unfortunately
        # easy to read in the opposite direction.
        automatic = "surface" if row["ground"] == "0" else "air"
        confusion[(manual, automatic)] += 1
        if manual != automatic:
            disagreements.append((enemy_type, manual, automatic, source))

    total = sum(confusion.values())
    correct = confusion[("surface", "surface")] + confusion[("air", "air")]
    accuracy = correct / total if total else 0.0

    # Family-aware leave-one-out audit.  Sprite bank is a hard boundary;
    # graphics/type adjacency and identical movement metadata identify the
    # component/animation family.  Distant matches are review-only.
    conflicted_refs = conflicted_family_references(labels, by_type)
    family_total = family_correct = 0
    family_misses: list[tuple[int, str, str, float]] = []
    for enemy_type, (manual, _source) in sorted(labels.items()):
        row = by_type[enemy_type]
        candidates = [
            other for other in labels
            if other != enemy_type
            and other not in conflicted_refs
            and same_family(row, by_type[other])
        ]
        if not candidates:
            continue
        nearest = min(candidates, key=lambda other: family_distance(row, by_type[other]))
        distance = family_distance(row, by_type[nearest])
        if distance > 10:
            continue
        predicted = labels[nearest][0]
        family_total += 1
        family_correct += predicted == manual
        if predicted != manual:
            family_misses.append((enemy_type, manual, predicted, distance))

    proposal_counts: Counter[str] = Counter()
    for enemy_type, row in by_type.items():
        if enemy_type in labels:
            proposal_counts["manual"] += 1
            continue
        candidates = [
            other for other in labels
            if other not in conflicted_refs
            and same_family(row, by_type[other])
        ]
        if not candidates:
            proposal_counts["review"] += 1
            continue
        nearest = min(candidates, key=lambda other: family_distance(row, by_type[other]))
        distance = family_distance(row, by_type[nearest])
        proposal_counts[labels[nearest][0] if distance <= 10 else "review"] += 1

    lines = [
        "# Episode 1 height automation audit",
        "",
        "The automatic broad-layer rule is Tyrian's authored enemy-ground bit: "
        "a zero low explosion-type bit means surface-attached; one means flying. "
        "Ground/building instances then sample the terrain or aerial platform "
        "beneath them. Explicit numeric heights remain manual refinements.",
        "",
        f"The raw ground-bit baseline agrees with **{correct}/{total} "
        f"({accuracy:.1%})** binary manual placements recovered from the two "
        "saved Episode 1 editing batches. It is useful evidence, but not "
        "reliable enough by itself.",
        "",
        "| Manual | Auto surface | Auto air |",
        "|---|---:|---:|",
        f"| Surface | {confusion[('surface', 'surface')]} | {confusion[('surface', 'air')]} |",
        f"| Air | {confusion[('air', 'surface')]} | {confusion[('air', 'air')]} |",
        "",
        "The family-aware classifier uses only references from the same sprite "
        "bank and auto-applies only at distance <= 10. In leave-one-out testing "
        f"it covers **{family_total}/{total}** manual placements and agrees on "
        f"**{family_correct}/{family_total} "
        f"({family_correct / family_total:.1%})**. More distant types are sent "
        "to review rather than guessed.",
        "",
        f"Across the full {len(by_type)}-type Episode 1 table this retains "
        f"{proposal_counts['manual']} manual references, proposes "
        f"{proposal_counts['surface']} additional surface and "
        f"{proposal_counts['air']} additional air types, and leaves "
        f"{proposal_counts['review']} review-only.",
        f"{len(conflicted_refs)} trusted references sit in mixed-label near "
        "families and are retained as overrides but quarantined from family propagation.",
        "",
        "Manual samples by save:",
        "",
    ]
    lines.extend(f"- {name}: {count}" for name, count in batch_counts)
    lines += ["", "Disagreements requiring a retained override:", ""]
    if disagreements:
        lines.extend(
            f"- Type {t}: manual `{manual}`, automatic `{automatic}` ({source})"
            for t, manual, automatic, source in disagreements
        )
    else:
        lines.append("- None")
    lines += ["", "High-confidence family-classifier misses:", ""]
    if family_misses:
        lines.extend(
            f"- Type {t}: manual `{manual}`, predicted `{predicted}` (distance {distance:.2f})"
            for t, manual, predicted, distance in family_misses
        )
    else:
        lines.append("- None")
    lines += [
        "",
        "Stacked-component policy:",
        "",
        "- A nonzero native link number defines an assembly for the current tick.",
        "- If any component is authored as ground, the whole assembly uses one "
        "topmost sampled surface, preventing tank bodies and turrets from splitting.",
        "- Low explicit component heights are treated as offsets from that shared "
        "surface, preserving the hand-authored stack. High explicit heights remain "
        "flying exceptions.",
        "- Unlinked flying enemies remain at their authored/default air height.",
        "",
        "The audit is intentionally non-destructive; it does not rewrite "
        "`godot/hover_heights.json`.",
        "",
    ]
    report = "\n".join(lines)
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(report, encoding="utf-8")
    else:
        print(report)

    if args.csv:
        args.csv.parent.mkdir(parents=True, exist_ok=True)
        with args.csv.open("w", newline="", encoding="utf-8") as handle:
            fields = ["type", "automatic", "manual", "agreement", "shapebank", "egraphic0", "size"]
            writer = csv.DictWriter(handle, fieldnames=fields)
            writer.writeheader()
            for enemy_type, row in sorted(by_type.items()):
                references = [
                    other for other in labels
                    if other != enemy_type
                    and same_family(row, by_type[other])
                ]
                nearest = min(references, key=lambda other: family_distance(row, by_type[other])) if references else None
                distance = family_distance(row, by_type[nearest]) if nearest is not None else float("inf")
                automatic = labels[nearest][0] if nearest is not None and distance <= 10 else "review"
                manual = labels.get(enemy_type, ("", ""))[0]
                writer.writerow({
                    "type": enemy_type,
                    "automatic": automatic,
                    "manual": manual,
                    "agreement": "" if not manual else str(manual == automatic).lower(),
                    "shapebank": row["shapebank"],
                    "egraphic0": row["egraphic0"],
                    "size": row["size"],
                })
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
