#!/usr/bin/env python3
"""
Check if forced flatten was enforced today and investigate why not.
Usage: python scripts/check_forced_flatten_today.py [trading_date]
"""
import json
import re
import sys
from pathlib import Path

QTSW2 = Path(__file__).resolve().parents[1]
LOG_DIR = QTSW2 / "logs" / "robot"
JOURNAL_DIR = QTSW2 / "logs" / "robot" / "journal"  # Slot journals + _forced_flatten_markers.json
EXEC_JOURNALS = QTSW2 / "data" / "execution_journals"  # Execution journal entries (intent fills)


def main():
    trading_date = sys.argv[1] if len(sys.argv) > 1 else "2026-03-04"
    print(f"=== Forced Flatten Diagnostic for {trading_date} ===\n")

    # 1. Search logs for forced flatten events
    events = []
    for p in sorted(LOG_DIR.glob("robot_*.jsonl")):
        try:
            for line in p.read_text(encoding="utf-8", errors="ignore").splitlines():
                if trading_date not in line:
                    continue
                if any(
                    x in line
                    for x in [
                        "FORCED_FLATTEN",
                        "SESSION_CLOSE",
                        "FlattenTrigger",
                        "ResolveAndSet",
                        "HandleForcedFlatten",
                        "FORCED_FLATTEN_SKIP",
                    ]
                ):
                    events.append((str(p.name), line[:500]))
        except Exception as e:
            print(f"  [skip {p.name}: {e}]")

    print("1. Forced flatten / session close events in logs:")
    if events:
        for fname, line in events[:30]:
            print(f"   [{fname}] {line[:200]}...")
    else:
        print("   NONE FOUND — forced flatten likely did not trigger or was not logged.")

    # 2. Check forced flatten markers file (in slot journal dir)
    markers_path = JOURNAL_DIR / "_forced_flatten_markers.json"
    print(f"\n2. Forced flatten markers file: {markers_path}")
    if markers_path.exists():
        try:
            data = json.loads(markers_path.read_text())
            for k, v in data.items():
                if trading_date in k:
                    print(f"   {k}: {v}")
        except Exception as e:
            print(f"   Error: {e}")
    else:
        print("   File does not exist — forced flatten was never marked as triggered.")

    # 3. Check slot journals for ForcedFlattenTimestamp (logs/robot/journal/tradingDate_stream.json)
    print(f"\n3. Slot journals with ForcedFlattenTimestamp for {trading_date}:")
    if JOURNAL_DIR.exists():
        found = 0
        for f in JOURNAL_DIR.glob(f"{trading_date}_*.json"):
            if f.name == "_forced_flatten_markers.json":
                continue
            try:
                j = json.loads(f.read_text())
                ts = j.get("ForcedFlattenTimestamp") or j.get("forced_flatten_timestamp")
                if ts:
                    print(f"   {f.name}: ForcedFlattenTimestamp = {ts}")
                    found += 1
            except Exception:
                pass
        if not found:
            print("   None — HandleForcedFlatten was not called for any stream.")
    else:
        print("   journal dir not found.")

    # 4. Root cause summary
    print("\n4. Likely root causes (if forced flatten did not run):")
    print("   a) Session close not resolved: ResolveAndSetSessionCloseIfNeeded() never ran or failed")
    print("      - Requires: State=Realtime, Bars.Count>0, parity spec loaded")
    print("      - Runs on Realtime transition and when trading date changes")
    print("   b) No uncommitted streams: All streams were already Committed")
    print("   c) Robot had 0 streams: TIMETABLE_TRADING_DATE_MISMATCH can leave stream_count=0")
    print("   d) Tick() not called during flatten window: No bars/ticks between 15:55–16:00 CT")
    print("   e) Holiday: SessionCloseResolver returns HasSession=false for holidays")
    print("\n   See: docs/robot/FORCED_FLATTEN_RUNDOWN.md")


if __name__ == "__main__":
    main()
