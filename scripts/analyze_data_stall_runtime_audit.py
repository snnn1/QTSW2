#!/usr/bin/env python3
"""
Offline answers for DATA_STALL runtime validation (reads data/watchdog/data_stall_runtime_audit.jsonl).

Binary questions (heuristic from captured JSON lines):
  1) GLOBAL_FEED_STALL_ENTER while any high-liq root age is below its implied fresh bar window?
  2) Real outage without ENTER? (needs external ground truth — not inferrable from file alone)
  3) reference stale_reason == no_loaded_roots frequency
  4) market_data_feed_alive_redundant_with_any_hi == false count (should be 0 with current code)
  5) Pre vs post noise: not comparable without archived pre-change jsonl

Usage:
  python scripts/analyze_data_stall_runtime_audit.py
  python scripts/analyze_data_stall_runtime_audit.py --file path/to/data_stall_runtime_audit.jsonl
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any, Dict, List

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from modules.watchdog.config import DATA_STALL_RUNTIME_AUDIT_JSONL
from modules.watchdog.config import data_stall_threshold_seconds_for_execution_instrument


def _fresh_by_age(age_sec: Any, root: str) -> bool:
    if age_sec is None:
        return False
    try:
        a = float(age_sec)
    except (TypeError, ValueError):
        return False
    thr = data_stall_threshold_seconds_for_execution_instrument(f"{root} 00-00")
    return a <= thr


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--file", type=Path, default=DATA_STALL_RUNTIME_AUDIT_JSONL)
    args = ap.parse_args()
    path: Path = args.file
    if not path.exists():
        print(f"No file at {path} — run watchdog to produce GLOBAL_FEED_STALL_* lines.")
        return 0

    enters: List[Dict[str, Any]] = []
    clears: List[Dict[str, Any]] = []
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            rec = json.loads(line)
        except json.JSONDecodeError:
            continue
        ev = rec.get("event")
        if ev == "GLOBAL_FEED_STALL_ENTER":
            enters.append(rec)
        elif ev == "GLOBAL_FEED_STALL_CLEAR":
            clears.append(rec)

    print(f"File: {path}")
    print(f"GLOBAL_FEED_STALL_ENTER count: {len(enters)}")
    print(f"GLOBAL_FEED_STALL_CLEAR count: {len(clears)}")

    suspicious = 0
    for rec in enters:
        ages = rec.get("high_liquidity_root_ages_seconds") or {}
        any_fresh = False
        for root, age in ages.items():
            if _fresh_by_age(age, str(root)):
                any_fresh = True
                break
        any_hi = rec.get("any_high_liquidity_updating")
        if any_fresh and not any_hi:
            suspicious += 1
    print(
        "\nQ1) ENTER events where some high-liq root age is within threshold but any_hi is false:",
        suspicious,
        "(>0 suggests logic bug or clock skew; investigate those lines)",
    )

    print(
        "\nQ2) Missed real outage? File alone cannot prove — correlate with broker/NT logs.",
    )

    no_loaded = 0
    total_class_rows = 0
    for rec in enters:
        for row in rec.get("reference_asset_classes") or []:
            total_class_rows += 1
            if row.get("stale_reason") == "no_loaded_roots":
                no_loaded += 1
    print(
        "\nQ3) Reference rows with stale_reason=no_loaded_roots:",
        f"{no_loaded}/{total_class_rows} (asset-class rows on ENTER events)",
    )

    non_redundant = sum(
        1
        for rec in enters
        if rec.get("market_data_feed_alive_redundant_with_any_hi") is False
    )
    print(
        "\nQ4) ENTER events where market_data_feed_alive_redundant_with_any_hi is false:",
        non_redundant,
        "(expected 0 while _market_data_feed_alive == any_hi)",
    )

    eng_diff = sum(
        1
        for rec in enters
        if rec.get("engine_alive_differs_from_market_data_feed_alive") is True
    )
    print("\nQ5) ENTER events where engine_alive != market_data_feed_alive:", eng_diff)

    durs = [rec.get("seconds_since_stall_start") for rec in clears if rec.get("seconds_since_stall_start") is not None]
    if durs:
        print("\nCLEAR duration seconds: min=%.1f max=%.1f" % (min(durs), max(durs)))

    print("\nQ6) Pre vs post noise: compare ENTER counts to an archived pre-change jsonl manually.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
