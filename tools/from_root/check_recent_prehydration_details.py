#!/usr/bin/env python3
"""Check recent PRE_HYDRATION events with details"""
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
                        if ts > '2026-01-21T02:28:00':
                            events.append(event)
                    except:
                        pass
    except:
        pass

events.sort(key=lambda e: e.get('ts_utc', ''))

print("="*80)
print("RECENT PRE_HYDRATION EVENTS (since 02:28:00)")
print("="*80)

# Group by stream
streams = {}
for e in events:
    if 'PRE_HYDRATION' in e.get('event', ''):
        payload = e.get('data', {}).get('payload', {})
        stream = payload.get('stream_id', e.get('stream', 'N/A'))
        if stream not in streams:
            streams[stream] = []
        streams[stream].append(e)

for stream, stream_events in sorted(streams.items()):
    if stream == 'N/A':
        continue
    print(f"\n=== Stream: {stream} ===")
    for e in stream_events[-5:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        event_name = e.get('event', 'N/A')
        print(f"  {ts} | {event_name}")

# Check for PRE_HYDRATION_CONDITION_CHECK to see what conditions are being evaluated
condition_checks = [e for e in events if 'PRE_HYDRATION_CONDITION_CHECK' in e.get('event', '')]
if condition_checks:
    print(f"\n=== PRE_HYDRATION_CONDITION_CHECK (last 5) ===")
    for e in condition_checks[-5:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        payload = e.get('data', {}).get('payload', {})
        bar_count = payload.get('bar_count', 'N/A')
        now_chicago = payload.get('now_chicago', 'N/A')[:19] if isinstance(payload.get('now_chicago'), str) else 'N/A'
        range_start = payload.get('range_start_chicago', 'N/A')[:19] if isinstance(payload.get('range_start_chicago'), str) else 'N/A'
        condition_met = payload.get('condition_met', 'N/A')
        will_transition = payload.get('will_transition', 'N/A')
        print(f"  {ts}")
        print(f"    Bar count: {bar_count}")
        print(f"    Now Chicago: {now_chicago}")
        print(f"    Range start: {range_start}")
        print(f"    Condition met: {condition_met}")
        print(f"    Will transition: {will_transition}")

print("\n" + "="*80)
