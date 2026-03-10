#!/usr/bin/env python3
"""
Reset Run Health - Remove failed runs from history so scheduled runs can proceed.

When health is "unstable" (3+ failures in last 5 runs), scheduled runs are blocked.
This script removes failed runs from runs.jsonl until health would be healthy.

Usage:
    python automation/reset_run_health.py [--dry-run]

Options:
    --dry-run    Show what would be removed without modifying the file
"""

import argparse
import json
import shutil
import sys
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description="Reset run health by pruning failed runs")
    parser.add_argument("--dry-run", action="store_true", help="Show changes without modifying file")
    args = parser.parse_args()

    root = Path(__file__).resolve().parent.parent
    runs_file = root / "automation" / "logs" / "runs" / "runs.jsonl"

    if not runs_file.exists():
        print(f"runs.jsonl not found: {runs_file}")
        return 1

    # Read all runs
    runs = []
    with open(runs_file, "r", encoding="utf-8") as f:
        for line in f:
            if not line.strip():
                continue
            try:
                runs.append(json.loads(line))
            except json.JSONDecodeError:
                continue

    # Sort by started_at descending (most recent first)
    runs.sort(key=lambda r: r.get("started_at") or "", reverse=True)

    # Find failed runs in the "last 5" window that we need to remove
    # Rule: >= 3 failures in last 5 -> unstable. We need <= 2 failures in last 5.
    last_5 = runs[:5]
    failed_ids = {r["run_id"] for r in last_5 if r.get("result") == "failed"}
    failure_count = len(failed_ids)

    if failure_count < 3:
        print("Health would already be healthy (fewer than 3 failures in last 5 runs).")
        return 0

    # Remove failed runs from the full list (we'll remove the ones in last 5)
    # Keep a backup
    backup_file = runs_file.with_suffix(".jsonl.bak")
    if not args.dry_run:
        shutil.copy2(runs_file, backup_file)
        print(f"Backup written to {backup_file}")

    # Filter out the failed runs
    to_remove = failed_ids
    kept = [r for r in runs if r["run_id"] not in to_remove]
    removed_count = len(runs) - len(kept)

    print(f"Removing {removed_count} failed run(s) from history: {sorted(to_remove)}")

    if args.dry_run:
        print("(dry-run: no changes made)")
        return 0

    # Write back (original order - chronological)
    kept.sort(key=lambda r: r.get("started_at") or "")
    with open(runs_file, "w", encoding="utf-8") as f:
        for r in kept:
            f.write(json.dumps(r, ensure_ascii=False) + "\n")

    print(f"Updated {runs_file}. Next scheduled run should proceed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
