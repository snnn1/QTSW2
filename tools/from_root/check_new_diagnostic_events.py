#!/usr/bin/env python3
"""Check for the new diagnostic events"""
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
                        if ts > '2026-01-21T02:40:00':
                            events.append(event)
                    except:
                        pass
    except:
        pass

events.sort(key=lambda e: e.get('ts_utc', ''))

targets = [
    'PRE_HYDRATION_COMPLETE_BLOCK_ENTERED',
    'PRE_HYDRATION_AFTER_COMPLETE_BLOCK',
    'PRE_HYDRATION_AFTER_VARIABLES',
    'PRE_HYDRATION_BEFORE_RANGE_DIAGNOSTIC',
    'PRE_HYDRATION_RANGE_START_DIAGNOSTIC',
    'PRE_HYDRATION_CONDITION_CHECK'
]

print("="*80)
print("DIAGNOSTIC FLOW CHECK")
print("="*80)

for target in targets:
    found = [e for e in events if target in e.get('event', '')]
    status = f"FOUND: {len(found)}" if found else "NOT FOUND"
    print(f"{target}: {status}")
    if found:
        latest = found[-1]
        ts = latest.get('ts_utc', 'N/A')[:19]
        payload = latest.get('data', {}).get('payload', {})
        stream = payload.get('stream_id', 'N/A')
        print(f"  Latest: {ts} | Stream: {stream}")

print("\n" + "="*80)
