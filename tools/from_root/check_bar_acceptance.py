#!/usr/bin/env python3
"""Quick check of bar acceptance/rejection events."""

import json
from pathlib import Path
from collections import Counter

logs_dir = Path("logs/robot")
engine_log = logs_dir / "robot_ENGINE.jsonl"

if not engine_log.exists():
    print(f"Log file not found: {engine_log}")
    exit(1)

bar_events = Counter()
instruments = Counter()

with open(engine_log, 'r', encoding='utf-8') as f:
    for line in f:
        line = line.strip()
        if not line:
            continue
        try:
            event = json.loads(line)
            event_type = event.get("event", "")
            if event_type in ["BAR_ACCEPTED", "BAR_DATE_MISMATCH", "BAR_PARTIAL_REJECTED", "BAR_REJECTED"]:
                bar_events[event_type] += 1
                payload = event.get("data", {}).get("payload", {})
                instrument = payload.get("instrument", "UNKNOWN")
                if instrument != "UNKNOWN":
                    instruments[instrument] += 1
        except:
            pass

print("="*60)
print("BAR ACCEPTANCE/REJECTION SUMMARY")
print("="*60)
print(f"\nTotal bar events by type:")
for event_type, count in sorted(bar_events.items()):
    print(f"  {event_type}: {count}")

print(f"\nInstruments with bar events: {len(instruments)}")
for inst, count in sorted(instruments.items()):
    print(f"  {inst}: {count}")

# Check for BAR_DATE_MISMATCH specifically
if bar_events["BAR_DATE_MISMATCH"] == 0:
    print("\n[SUCCESS] No BAR_DATE_MISMATCH events - fix appears to be working!")
else:
    print(f"\n[WARNING] Found {bar_events['BAR_DATE_MISMATCH']} BAR_DATE_MISMATCH events")
