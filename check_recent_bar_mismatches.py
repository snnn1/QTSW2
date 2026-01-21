#!/usr/bin/env python3
"""Check recent BAR_DATE_MISMATCH events to see if they're from after restart."""

import json
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict

logs_dir = Path("logs/robot")
engine_log = logs_dir / "robot_ENGINE.jsonl"

if not engine_log.exists():
    print(f"Log file not found: {engine_log}")
    exit(1)

# Get all BAR_DATE_MISMATCH events with timestamps
mismatch_events = []

with open(engine_log, 'r', encoding='utf-8') as f:
    for line in f:
        line = line.strip()
        if not line:
            continue
        try:
            event = json.loads(line)
            if event.get("event") == "BAR_DATE_MISMATCH":
                payload = event.get("data", {}).get("payload", {})
                mismatch_events.append({
                    "timestamp": event.get("ts_utc", ""),
                    "instrument": payload.get("instrument", "UNKNOWN"),
                    "bar_chicago": payload.get("bar_chicago", ""),
                    "session_start_chicago": payload.get("session_start_chicago", ""),
                    "session_end_chicago": payload.get("session_end_chicago", ""),
                    "rejection_reason": payload.get("rejection_reason", ""),
                    "active_trading_date": payload.get("active_trading_date", "")
                })
        except Exception as e:
            pass

print(f"Total BAR_DATE_MISMATCH events: {len(mismatch_events)}")

if mismatch_events:
    # Show most recent 5 events
    print("\nMost recent 5 BAR_DATE_MISMATCH events:")
    for i, event in enumerate(mismatch_events[-5:], 1):
        print(f"\n{i}. Timestamp: {event['timestamp']}")
        print(f"   Instrument: {event['instrument']}")
        print(f"   Active Trading Date: {event['active_trading_date']}")
        print(f"   Bar Chicago: {event['bar_chicago']}")
        print(f"   Session Start: {event['session_start_chicago']}")
        print(f"   Session End: {event['session_end_chicago']}")
        print(f"   Rejection Reason: {event['rejection_reason']}")
    
    # Check if events have session window fields
    with_fields = sum(1 for e in mismatch_events if e['session_start_chicago'] and e['session_end_chicago'])
    print(f"\nEvents with session window fields: {with_fields}/{len(mismatch_events)}")
    
    # Group by instrument
    by_instrument = defaultdict(int)
    for event in mismatch_events:
        by_instrument[event['instrument']] += 1
    print(f"\nBAR_DATE_MISMATCH by instrument:")
    for inst, count in sorted(by_instrument.items()):
        print(f"  {inst}: {count}")
