#!/usr/bin/env python3
"""
Check if break-even detection is working.
Searches robot logs for BE_PATH_ACTIVE, BE_EVALUATION_TICK, BE_TRIGGER_REACHED, etc.
"""
import json
import sys
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict

ROOT = Path(__file__).parent
LOG_DIR = ROOT / "logs" / "robot"

BE_EVENTS = [
    "ONMARKETDATA_LAST_DIAG",  # Diagnostic: OnMarketData(Last) fired in Realtime (shows position state)
    "BE_PATH_ACTIVE",       # BE path running (rate-limited 1/min) - proves OnMarketData is firing
    "BE_EVALUATION_TICK",    # BE scan ran (rate-limited 1/sec) - proves CheckBreakEvenTriggersTickBased ran
    "BE_TRIGGER_REACHED",   # Stop modified to BE successfully
    "BE_TRIGGER_RETRY_NEEDED",  # Trigger reached, stop not found yet (retry)
    "BE_TRIGGER_FAILED",    # Modify failed (non-retryable)
    "BE_SKIP_STOP_ALREADY_TIGHTER",  # Only-tighten guard hit
    "BE_TRIGGER_TIMEOUT_ERROR",  # Trigger reached 5+ sec ago, no modify
    "REALTIME_STATE_REACHED",  # Strategy in Realtime (prerequisite for BE)
]
BE_GATE_BLOCKED = "BE_GATE_BLOCKED"  # Path blocked - check reason for pre-flight

def main():
    print("=" * 70)
    print("BREAK-EVEN DETECTION CHECK")
    print("=" * 70)
    print(f"Time: {datetime.now().isoformat()}")
    print(f"Log dir: {LOG_DIR}")
    print()

    if not LOG_DIR.exists():
        print(f"ERROR: Log directory not found: {LOG_DIR}")
        print("  - Ensure the strategy has been enabled and NinjaTrader has run")
        print("  - Logs are written to logs/robot/ when the strategy runs")
        sys.exit(1)

    # Find all robot_*.jsonl files
    log_files = list(LOG_DIR.glob("robot_*.jsonl"))
    # Exclude rotated/archived (robot_X_timestamp.jsonl)
    log_files = [f for f in log_files if "_" not in f.stem.split("robot_")[-1] or f.stem.count("_") == 1]

    if not log_files:
        print("No robot log files found.")
        print("  - Strategy may not have run yet, or logs are in a different location")
        sys.exit(1)

    print(f"Scanning {len(log_files)} log file(s)...")
    print()

    counts = defaultdict(int)
    latest = {}
    samples = defaultdict(list)  # Keep last 3 samples per event type

    for log_path in sorted(log_files):
        try:
            with open(log_path, "r", encoding="utf-8-sig") as f:
                lines = f.readlines()
        except Exception as e:
            print(f"  Skip {log_path.name}: {e}")
            continue

        # Check last 2000 lines (recent activity)
        recent = lines[-2000:] if len(lines) > 2000 else lines

        for line in recent:
            line = line.strip()
            if not line:
                continue
            try:
                e = json.loads(line)
            except json.JSONDecodeError:
                continue

            # Handle both formats: "event" (RobotLogEvent) or "event_type" (legacy)
            event_type = e.get("event") or e.get("event_type") or (e.get("data") or {}).get("event_type") if isinstance(e.get("data"), dict) else None
            if not event_type:
                continue

            if event_type in BE_EVENTS or event_type == BE_GATE_BLOCKED:
                counts[event_type] += 1
                ts = e.get("ts_utc", e.get("timestamp_utc", ""))[:19]
                latest[event_type] = ts
                if len(samples[event_type]) < 3:
                    samples[event_type].append({"file": log_path.name, "ts": ts, "data": e.get("data", e)})

    # Report
    print("BE-RELATED EVENTS (last 2000 lines per file)")
    print("-" * 70)
    if not counts:
        print("  NONE FOUND")
        print()
        print("  This could mean:")
        print("  1. Strategy is not in Realtime yet (check for REALTIME_STATE_REACHED)")
        print("  2. No position / no intents needing BE (active_intent_count=0)")
        print("  3. OnMarketData(Last) not firing for this instrument")
        print("  4. Logs are in a different directory or not yet written")
        print()
        print("  Next steps:")
        print("  - Ensure strategy is enabled and chart is in Realtime")
        print("  - Wait for an entry fill - BE only runs when in position")
        print("  - Check NinjaTrader Output window for errors")
    else:
        for evt in BE_EVENTS:
            c = counts.get(evt, 0)
            if c > 0:
                ts = latest.get(evt, "")
                print(f"  {evt}: {c} (latest: {ts})")
        print()

        # Key diagnostics
        if counts.get("BE_PATH_ACTIVE"):
            print("  BE_PATH_ACTIVE present -> OnMarketData is firing when in position")
        if counts.get("BE_EVALUATION_TICK"):
            print("  BE_EVALUATION_TICK present -> BE scan is running")
        if counts.get("BE_TRIGGER_REACHED"):
            print("  BE_TRIGGER_REACHED present -> BE successfully modified a stop")
        if counts.get("REALTIME_STATE_REACHED") and not counts.get("BE_PATH_ACTIVE"):
            print("  REALTIME reached but no BE_PATH_ACTIVE -> may be flat (no position)")

        # Sample BE_PATH_ACTIVE or BE_EVALUATION_TICK
        # Pre-flight verdict for tomorrow
        print()
        print("PRE-FLIGHT VERDICT (for tomorrow)")
        print("-" * 70)
        if counts.get(BE_GATE_BLOCKED):
            reasons = set()
            for s in samples.get(BE_GATE_BLOCKED, []):
                d = (s.get("data") or s) if isinstance(s, dict) else {}
                if isinstance(d, dict) and "blocking_reason" in d:
                    reasons.add(str(d["blocking_reason"]))
            print(f"  WARN: BE_GATE_BLOCKED seen ({counts[BE_GATE_BLOCKED]}x). Reasons: {reasons or 'unknown'}")
            print("  -> Fix blocked reason before market open")
        if counts.get("REALTIME_STATE_REACHED") and counts.get("BE_PATH_ACTIVE"):
            print("  OK: REALTIME + BE_PATH_ACTIVE -> BE path was active when in position")
        elif counts.get("REALTIME_STATE_REACHED") and not counts.get("BE_PATH_ACTIVE"):
            print("  INFO: REALTIME reached but no BE_PATH_ACTIVE -> likely flat (no position) or blocked")
        if counts.get("BE_TRIGGER_REACHED"):
            print("  OK: BE_TRIGGER_REACHED -> BE successfully modified a stop")
        if not counts.get("REALTIME_STATE_REACHED"):
            print("  WARN: No REALTIME_STATE_REACHED -> strategy may not have run to Realtime")

        for evt in ["BE_PATH_ACTIVE", "BE_EVALUATION_TICK", "BE_TRIGGER_REACHED"]:
            if samples[evt]:
                print()
                print(f"  Sample {evt}:")
                s = samples[evt][-1]
                d = s.get("data") or {}
                if isinstance(d, dict):
                    for k, v in list(d.items())[:8]:
                        print(f"    {k}: {v}")
                print(f"    (from {s.get('file', '?')})")

    print()
    print("=" * 70)


if __name__ == "__main__":
    main()
