#!/usr/bin/env python3
"""Check latest log entries"""
import json
from pathlib import Path

log_file = Path("logs/robot/robot_ENGINE.jsonl")
if not log_file.exists():
    print("Log file not found")
    exit(1)

events = []
with open(log_file, 'r', encoding='utf-8-sig') as f:
    for line in f:
        line = line.strip()
        if line:
            try:
                events.append(json.loads(line))
            except:
                pass

recent = events[-30:] if len(events) > 30 else events

print("="*80)
print("LATEST LOG ENTRIES (last 30)")
print("="*80)

for e in recent[-20:]:
    ts = e.get('ts_utc', 'N/A')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', 'N/A')
    event = e.get('event', 'N/A')
    payload = e.get('data', {}).get('payload', {})
    stream = payload.get('stream', payload.get('stream_id', 'N/A'))
    print(f"  {ts} | {event:45} | Stream: {stream}")

# Check for any diagnostic events in entire log
all_diagnostic = [e for e in events if any(x in e.get('event', '') for x in ['TICK_TRACE', 'PRE_HYDRATION_HANDLER_TRACE', 'RANGE_START_INITIALIZED', 'PRE_HYDRATION_RANGE_START_DIAGNOSTIC'])]
print(f"\n=== TOTAL DIAGNOSTIC EVENTS IN ENTIRE LOG ===")
print(f"Found {len(all_diagnostic)} diagnostic events")

if all_diagnostic:
    print("\nDiagnostic events found:")
    for e in all_diagnostic[-10:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        event = e.get('event', 'N/A')
        print(f"  {ts} | {event}")

print("\n" + "="*80)
