#!/usr/bin/env python3
"""
CPU / crash diagnostic helper for QTSW2 (watchdog + robot log volume + profiling flags).

Does NOT replace NT/PerfView profiling — use when investigating high CPU or instability.

Run from repo root:
  python automation/scripts/cpu_crash_diagnostic.py
  python automation/scripts/cpu_crash_diagnostic.py --lines 100000
"""
from __future__ import annotations

import argparse
import collections
import json
import sys
from pathlib import Path


def _repo_root() -> Path:
    return Path(__file__).resolve().parents[2]


def histogram_feed(path: Path, max_lines: int) -> tuple[list[tuple[str, int]], int]:
    if not path.exists():
        return [], 0
    text = path.read_text(encoding="utf-8", errors="replace")
    lines = text.splitlines()
    sample = lines[-max_lines:] if len(lines) > max_lines else lines
    ctr: collections.Counter[str] = collections.Counter()
    parse_err = 0
    for ln in sample:
        try:
            o = json.loads(ln)
            et = o.get("event_type") or o.get("event") or ""
            ctr[str(et)] += 1
        except json.JSONDecodeError:
            parse_err += 1
    if parse_err:
        ctr["__JSON_PARSE_ERROR__"] = parse_err
    return ctr.most_common(30), len(sample)


def scan_robot_logs_for_strings(root: Path, patterns: list[str], tail_bytes: int = 2_000_000) -> dict[str, int]:
    out: dict[str, int] = {p: 0 for p in patterns}
    logs_dir = root / "logs" / "robot"
    if not logs_dir.is_dir():
        return out
    for fp in sorted(logs_dir.glob("robot_*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)[:3]:
        try:
            data = fp.read_bytes()
            if len(data) > tail_bytes:
                data = data[-tail_bytes:]
            chunk = data.decode("utf-8", errors="replace")
            for p in patterns:
                out[p] += chunk.count(p)
        except OSError:
            continue
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description="CPU/crash diagnostic (log histogram + profiling hints)")
    ap.add_argument("--lines", type=int, default=50_000, help="Last N lines of frontend_feed.jsonl to sample")
    args = ap.parse_args()
    root = _repo_root()
    feed = root / "logs" / "robot" / "frontend_feed.jsonl"
    flag = root / "data" / "engine_cpu_profile.enabled"

    print("=== QTSW2 CPU / crash diagnostic ===\n")
    print(f"Repo: {root}")
    print(f"engine_cpu_profile flag: {'PRESENT (profiling can emit ENGINE_CPU_PROFILE)' if flag.exists() else 'MISSING - create empty file to enable (see docs/robot/ENGINE_CPU_PROFILE.md)'}\n")

    top, n = histogram_feed(feed, args.lines)
    print(f"frontend_feed.jsonl - last {n} lines, top event types:")
    if not top:
        print("  (file missing or empty)\n")
    else:
        for name, cnt in top:
            print(f"  {cnt:8d}  {name}")
        print()

    patterns = ["ENGINE_CPU_PROFILE", "ADOPTION_SCAN_SUMMARY", "ADOPTION_SCAN_START", "EXECUTION_BLOCKED", "DISCONNECT_FAIL_CLOSED"]
    hits = scan_robot_logs_for_strings(root, patterns)
    print("robot_*.jsonl (tail of 3 newest files, ~2MB each) substring counts:")
    for k, v in hits.items():
        print(f"  {v:8d}  {k}")
    print()

    print("Next steps (proper testing on the trading box):\n")
    print("  1) Robot hotspots: create data/engine_cpu_profile.enabled, restart NT, reproduce; grep ENGINE_CPU_PROFILE / ADOPTION_SCAN_SUMMARY in robot logs.")
    print("  2) Watchdog hotspots: set env WATCHDOG_CPU_DIAG=1, restart watchdog; watch WATCHDOG_CPU_DIAG lines (docs/watchdog/CPU_DIAG.md).")
    print("  3) If NT CPU high but robot profile low: use PerfView / NT built-in profiling on NinjaTrader.exe.")
    print("  4) See docs/robot/audits/IEA_CPU_RUNAWAY_FORENSIC_AUDIT_2026-03-23.md for adoption/order-scan theory.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
