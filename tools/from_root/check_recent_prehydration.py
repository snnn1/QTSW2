#!/usr/bin/env python3
"""Check recent PRE_HYDRATION events"""
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
                        events.append(json.loads(line))
                    except:
                        pass
    except:
        pass

events.sort(key=lambda e: e.get('ts_utc', ''))
recent = [e for e in events if e.get('ts_utc', '') > '2026-01-21T02:23:00']

print("="*80)
print("RECENT PRE_HYDRATION EVENTS (since 02:23:00)")
print("="*80)
print(f"\nTotal events since 02:23:00: {len(recent)}")

pre_hyd_events = [e for e in recent if 'PRE_HYDRATION' in e.get('event', '')]
print(f"\nPRE_HYDRATION events: {len(pre_hyd_events)}")

if pre_hyd_events:
    print("\n=== All PRE_HYDRATION events ===")
    for e in pre_hyd_events:
        ts = e.get('ts_utc', 'N/A')[:19] if isinstance(e.get('ts_utc'), str) else 'N/A'
        event_name = e.get('event', 'N/A')
        payload = e.get('data', {}).get('payload', {})
        stream = payload.get('stream_id', e.get('stream', 'N/A'))
        print(f"  {ts} | {event_name} | Stream: {stream}")
else:
    print("\n[INFO] No PRE_HYDRATION events found since 02:23:00")
    print("  -> PRE_HYDRATION_HANDLER_TRACE is rate-limited (5 min)")
    print("  -> PRE_HYDRATION_COMPLETE_SET should appear immediately if code is compiled")

# Check for TICK_METHOD_ENTERED to confirm Tick() is being called
tick_entered = [e for e in recent if 'TICK_METHOD_ENTERED' in e.get('event', '')]
print(f"\nTICK_METHOD_ENTERED events since 02:23:00: {len(tick_entered)}")
if tick_entered:
    print(f"  Latest: {tick_entered[-1].get('ts_utc', 'N/A')[:19]}")

print("\n" + "="*80)
