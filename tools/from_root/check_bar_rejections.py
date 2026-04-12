#!/usr/bin/env python3
"""Check BAR_DATE_MISMATCH events to see if they're legitimate"""
import json
from pathlib import Path

log_file = Path("logs/robot/robot_ENGINE.jsonl")
if not log_file.exists():
    print(f"Log file not found: {log_file}")
    exit(1)

events = []
with open(log_file, 'r', encoding='utf-8') as f:
    for line in f:
        line = line.strip()
        if line:
            try:
                events.append(json.loads(line))
            except:
                pass

mismatches = [e for e in events[-150:] if 'BAR_DATE_MISMATCH' in e.get('event', '')]
print(f"Found {len(mismatches)} BAR_DATE_MISMATCH events in last 150 events")

if mismatches:
    print("\n=== SAMPLE BAR_DATE_MISMATCH EVENTS ===")
    for i, e in enumerate(mismatches[:5]):
        payload = e.get('data', {}).get('payload', {})
        print(f"\nEvent {i+1}:")
        print(f"  Rejection reason: {payload.get('rejection_reason', 'N/A')}")
        print(f"  Bar Chicago: {payload.get('bar_chicago', 'N/A')}")
        print(f"  Session start: {payload.get('session_start_chicago', 'N/A')}")
        print(f"  Session end: {payload.get('session_end_chicago', 'N/A')}")
        print(f"  Trading date: {payload.get('active_trading_date', 'N/A')}")
        print(f"  Instrument: {payload.get('instrument', 'N/A')}")
        print(f"  Timestamp: {e.get('ts_utc', 'N/A')[:19]}")

# Check engine status
recent = events[-100:]
trading_date_locked = [e for e in recent if 'TRADING_DATE_LOCKED' in e.get('event', '')]
engine_start = [e for e in recent if 'ENGINE_START' in e.get('event', '')]
streams_armed = [e for e in recent if 'STREAM_ARMED' in e.get('event', '') or 'ARMED' in e.get('event', '')]

print(f"\n=== ENGINE STATUS ===")
print(f"ENGINE_START events: {len(engine_start)}")
print(f"TRADING_DATE_LOCKED events: {len(trading_date_locked)}")
print(f"STREAM_ARMED events: {len(streams_armed)}")

if trading_date_locked:
    latest = trading_date_locked[-1]
    payload = latest.get('data', {}).get('payload', {})
    print(f"\nLatest trading date locked: {payload.get('trading_date', 'N/A')}")
    print(f"Source: {payload.get('source', 'N/A')}")
    print(f"Timestamp: {latest.get('ts_utc', 'N/A')[:19]}")
