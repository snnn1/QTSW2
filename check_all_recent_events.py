#!/usr/bin/env python3
"""Check all recent events to see what's actually appearing"""
import json
from pathlib import Path

log_dir = Path("logs/robot")
events = []
for log_file in log_dir.glob("robot_*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        event = json.loads(line)
                        ts = event.get('ts_utc', '')
                        if ts > '2026-01-21T02:41:00':
                            events.append(event)
                    except:
                        pass
    except:
        pass

events.sort(key=lambda e: e.get('ts_utc', ''))

# Get all unique event names
event_names = set()
for e in events:
    event_name = e.get('event', '')
    if 'PRE_HYDRATION' in event_name or 'TICK' in event_name:
        event_names.add(event_name)

print("="*80)
print("ALL PRE_HYDRATION AND TICK EVENTS (since 02:41:00)")
print("="*80)
print(f"\nTotal events: {len(events)}")
print(f"Unique event names: {len(event_names)}")

print("\n=== Event Name Counts ===")
event_counts = {}
for e in events:
    name = e.get('event', '')
    if 'PRE_HYDRATION' in name or 'TICK' in name:
        event_counts[name] = event_counts.get(name, 0) + 1

for name, count in sorted(event_counts.items(), key=lambda x: -x[1]):
    print(f"  {name}: {count}")

# Check for the specific events we're looking for
targets = [
    'PRE_HYDRATION_BEFORE_RANGE_DIAGNOSTIC',
    'PRE_HYDRATION_RANGE_START_DIAGNOSTIC',
    'PRE_HYDRATION_RANGE_START_DIAGNOSTIC_ERROR',
    'PRE_HYDRATION_RANGE_START_DIAGNOSTIC_NULL_CHECK_FAILED'
]

print("\n=== Target Diagnostic Events ===")
for target in targets:
    found = [e for e in events if target in e.get('event', '')]
    status = f"FOUND: {len(found)}" if found else "NOT FOUND"
    print(f"  {target}: {status}")
    if found:
        latest = found[-1]
        ts = latest.get('ts_utc', 'N/A')[:19]
        print(f"    Latest: {ts}")

print("\n" + "="*80)
