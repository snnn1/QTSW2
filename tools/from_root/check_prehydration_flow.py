#!/usr/bin/env python3
"""Check PRE_HYDRATION flow to understand why RANGE_START_DIAGNOSTIC isn't appearing"""
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

# Focus on one stream to see the flow
rty1_events = [e for e in events if e.get('data', {}).get('payload', {}).get('stream_id') == 'RTY1' or 
               (e.get('stream') == 'RTY1' and 'PRE_HYDRATION' in e.get('event', ''))]

print("="*80)
print("RTY1 PRE_HYDRATION FLOW ANALYSIS")
print("="*80)
print(f"\nTotal RTY1 PRE_HYDRATION events since 02:28:00: {len(rty1_events)}")

if rty1_events:
    print("\n=== RTY1 Event Sequence (last 20) ===")
    for e in rty1_events[-20:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        event_name = e.get('event', 'N/A')
        print(f"  {ts} | {event_name}")

# Check for any ERROR events
error_events = [e for e in events if 'ERROR' in e.get('event', '') and 'PRE_HYDRATION' in e.get('event', '')]
print(f"\n=== PRE_HYDRATION ERROR Events ===")
print(f"Total: {len(error_events)}")
if error_events:
    for e in error_events[-10:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        event_name = e.get('event', 'N/A')
        payload = e.get('data', {}).get('payload', {})
        stream = payload.get('stream_id', e.get('stream', 'N/A'))
        error = payload.get('error', 'N/A')
        print(f"  {ts} | {event_name} | Stream: {stream}")
        print(f"    Error: {error}")

# Check PRE_HYDRATION_CONDITION_CHECK for RTY1
condition_checks = [e for e in events if 'PRE_HYDRATION_CONDITION_CHECK' in e.get('event', '')]
if condition_checks:
    print(f"\n=== PRE_HYDRATION_CONDITION_CHECK (last 3) ===")
    for e in condition_checks[-3:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        payload = e.get('data', {}).get('payload', {})
        bar_count = payload.get('bar_count', 'N/A')
        now_chicago = payload.get('now_chicago', 'N/A')
        range_start = payload.get('range_start_chicago', 'N/A')
        condition_met = payload.get('condition_met', 'N/A')
        will_transition = payload.get('will_transition', 'N/A')
        print(f"  {ts}")
        print(f"    Bar count: {bar_count}")
        print(f"    Now Chicago: {now_chicago}")
        print(f"    Range start: {range_start}")
        print(f"    Condition met: {condition_met}")
        print(f"    Will transition: {will_transition}")

print("\n" + "="*80)
