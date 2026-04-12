#!/usr/bin/env python3
"""
Roll back analyzed data and master matrix to a cutoff date.

Usage:
  python scripts/rollback_data_to_date.py --cutoff 2026-03-06

What it does:
  1. Filters analyzed parquet files to keep only rows with Date <= cutoff
  2. Updates merger_processed.json to remove entries after cutoff (so re-merge won't re-add them)
  3. Rebuilds the master matrix from the rolled-back analyzed data

Requires: data/analyzed, data/master_matrix, merger
"""
import argparse
import json
import subprocess
import sys
from pathlib import Path

import pandas as pd

BASE = Path(__file__).resolve().parent.parent
ANALYZED_DIR = BASE / "data" / "analyzed"
MATRIX_DIR = BASE / "data" / "master_matrix"
PROCESSED_LOG = BASE / "data" / "merger_processed.json"


def rollback_analyzed(cutoff_date: str) -> int:
    """Filter analyzed parquet files to keep only rows with Date <= cutoff_date. Returns count of files modified."""
    cutoff = pd.Timestamp(cutoff_date)
    modified = 0

    for parquet_path in ANALYZED_DIR.rglob("*.parquet"):
        try:
            df = pd.read_parquet(parquet_path)
            if df.empty or "Date" not in df.columns:
                continue

            # Normalize Date for comparison
            df["Date"] = pd.to_datetime(df["Date"], errors="coerce")
            before = len(df)
            df = df[df["Date"] <= cutoff].copy()
            after = len(df)

            if after < before:
                df.to_parquet(parquet_path, index=False)
                modified += 1
                print(f"  Rolled back {parquet_path.relative_to(BASE)}: {before} -> {after} rows")
        except Exception as e:
            print(f"  ERROR {parquet_path}: {e}", file=sys.stderr)

    return modified


def rollback_merger_log(cutoff_date: str) -> None:
    """Remove merger_processed entries for dates after cutoff."""
    if not PROCESSED_LOG.exists():
        return

    with open(PROCESSED_LOG) as f:
        log = json.load(f)

    cutoff_str = cutoff_date  # e.g. 2026-03-06
    for key in ["analyzer", "manual_analyzer"]:
        if key not in log:
            continue
        original = log[key]
        filtered = [p for p in original if _path_date_le(p, cutoff_str)]
        removed = len(original) - len(filtered)
        if removed > 0:
            log[key] = filtered
            print(f"  Removed {removed} entries from merger log ({key})")
    with open(PROCESSED_LOG, "w") as f:
        json.dump(log, f, indent=2)


def _path_date_le(path_str: str, cutoff: str) -> bool:
    """True if path's date (e.g. 2026-03-09) <= cutoff (e.g. 2026-03-06). Keep entries on or before cutoff."""
    try:
        # Paths like .../analyzer_temp/2026-03-09 or ...\2026-03-09
        parts = path_str.replace("\\", "/").split("/")
        for part in parts:
            if len(part) == 10 and part[4] == "-" and part[7] == "-" and part.replace("-", "").isdigit():
                return part <= cutoff
    except Exception:
        pass
    return True  # Keep if we can't parse (conservative)


def rebuild_matrix() -> bool:
    """Run master matrix build. Returns True on success."""
    script = BASE / "scripts" / "maintenance" / "run_matrix_and_timetable.py"
    if not script.exists():
        # Fallback: try automation pipeline
        script = BASE / "automation" / "run_pipeline_standalone.py"
    if not script.exists():
        print("  Could not find matrix build script. Run manually: python scripts/maintenance/run_matrix_and_timetable.py")
        return False

    print("  Rebuilding master matrix...")
    result = subprocess.run([sys.executable, str(script)], cwd=str(BASE))
    return result.returncode == 0


def main():
    ap = argparse.ArgumentParser(description="Roll back data to a cutoff date (YYYY-MM-DD)")
    ap.add_argument("--cutoff", required=True, help="Cutoff date (e.g. 2026-03-06)")
    ap.add_argument("--analyzed-only", action="store_true", help="Only roll back analyzed, skip matrix rebuild")
    args = ap.parse_args()

    cutoff = args.cutoff
    print(f"Rolling back to {cutoff}")

    if not ANALYZED_DIR.exists():
        print("ERROR: data/analyzed not found")
        sys.exit(1)

    print("1. Rolling back analyzed parquet files...")
    n = rollback_analyzed(cutoff)
    print(f"   Modified {n} file(s)")

    print("2. Updating merger_processed.json...")
    rollback_merger_log(cutoff)

    if args.analyzed_only:
        print("3. Skipping matrix rebuild (--analyzed-only)")
    else:
        print("3. Rebuilding master matrix...")
        if not rebuild_matrix():
            print("   Matrix rebuild failed. Run manually if needed.")
            sys.exit(1)

    print("Done.")


if __name__ == "__main__":
    main()
