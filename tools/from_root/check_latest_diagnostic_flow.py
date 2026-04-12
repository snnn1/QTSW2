#!/usr/bin/env python3
"""Comprehensive check of all diagnostic events in the PRE_HYDRATION flow"""
import json
from pathlib import Path
from datetime import datetime, timezone

log_dir = Path("logs/robot")
all_events = []

# Collect all events from all log files
for log_file in sorted(log_dir.glob("robot_*.jsonl")):
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line_num, line in enumerate(f, 1):
                line = line.strip()
                if line:
                    try:
                        event = json.loads(line)
                        ts = event.get('ts_utc', '')
                        # Get events from last hour
                        if ts > '2026-01-21T01:42:00':
                            event['_source_file'] = log_file.name
                            event['_line_num'] = line_num
                            all_events.append(event)
                    except Exception as e:
                        pass
    except Exception as e:
        pass

# Sort by timestamp
all_events.sort(key=lambda e: e.get('ts_utc', ''))

print("="*80)
print("COMPREHENSIVE PRE_HYDRATION DIAGNOSTIC FLOW CHECK")
print("="*80)
print(f"\nTotal events since 01:42:00: {len(all_events)}")

# Find all PRE_HYDRATION related events
prehydration_events = [e for e in all_events if 'PRE_HYDRATION' in e.get('event', '')]
tick_events = [e for e in all_events if 'TICK' in e.get('event', '')]

print(f"PRE_HYDRATION events: {len(prehydration_events)}")
print(f"TICK events: {len(tick_events)}")

# Group by event type
event_types = {}
for e in prehydration_events + tick_events:
    event_name = e.get('event', '')
    if event_name not in event_types:
        event_types[event_name] = []
    event_types[event_name].append(e)

print("\n=== Event Type Summary ===")
for event_name in sorted(event_types.keys()):
    events = event_types[event_name]
    print(f"\n{event_name}: {len(events)} events")
    if events:
        latest = events[-1]
        ts = latest.get('ts_utc', 'N/A')[:19]
        payload = latest.get('data', {}).get('payload', {})
        stream = payload.get('stream_id', 'N/A')
        print(f"  Latest: {ts} | Stream: {stream} | File: {latest.get('_source_file', 'N/A')}")

# Check for the specific new diagnostic events
target_events = [
    'PRE_HYDRATION_BEFORE_RANGE_DIAGNOSTIC',
    'PRE_HYDRATION_RANGE_START_DIAGNOSTIC',
    'PRE_HYDRATION_RANGE_START_DIAGNOSTIC_ERROR',
    'PRE_HYDRATION_RANGE_START_DIAGNOSTIC_NULL_CHECK_FAILED'
]

print("\n=== Target New Diagnostic Events ===")
for target in target_events:
    found = event_types.get(target, [])
    if found:
        print(f"\n{target}: FOUND {len(found)} events")
        print("  Last 5 events:")
        for e in found[-5:]:
            ts = e.get('ts_utc', 'N/A')[:19]
            payload = e.get('data', {}).get('payload', {})
            stream = payload.get('stream_id', 'N/A')
            print(f"    {ts} | Stream: {stream}")
            if target == 'PRE_HYDRATION_RANGE_START_DIAGNOSTIC':
                range_start = payload.get('range_start_chicago_raw', 'N/A')
                is_default = payload.get('range_start_is_default', 'N/A')
                print(f"      RangeStart: {range_start[:19] if isinstance(range_start, str) else range_start} | IsDefault: {is_default}")
            elif target == 'PRE_HYDRATION_RANGE_START_DIAGNOSTIC_ERROR':
                error = payload.get('error', 'N/A')
                ex_type = payload.get('exception_type', 'N/A')
                print(f"      Error: {error}")
                print(f"      Exception: {ex_type}")
            elif target == 'PRE_HYDRATION_RANGE_START_DIAGNOSTIC_NULL_CHECK_FAILED':
                log_null = payload.get('log_null', 'N/A')
                time_null = payload.get('time_null', 'N/A')
                print(f"      log_null: {log_null} | time_null: {time_null}")
    else:
        print(f"{target}: NOT FOUND")

# Check the flow: PRE_HYDRATION_COMPLETE_BLOCK_ENTERED -> PRE_HYDRATION_BEFORE_RANGE_DIAGNOSTIC
print("\n=== Flow Analysis ===")
complete_block = event_types.get('PRE_HYDRATION_COMPLETE_BLOCK_ENTERED', [])
before_range = event_types.get('PRE_HYDRATION_BEFORE_RANGE_DIAGNOSTIC', [])

if complete_block and not before_range:
    print("ISSUE: PRE_HYDRATION_COMPLETE_BLOCK_ENTERED exists but PRE_HYDRATION_BEFORE_RANGE_DIAGNOSTIC does not")
    print("  This suggests the code between these two logs is not executing")
    print(f"  Last PRE_HYDRATION_COMPLETE_BLOCK_ENTERED: {complete_block[-1].get('ts_utc', 'N/A')[:19]}")
elif complete_block and before_range:
    print("FLOW OK: Both events found")
    print(f"  PRE_HYDRATION_COMPLETE_BLOCK_ENTERED: {len(complete_block)} events")
    print(f"  PRE_HYDRATION_BEFORE_RANGE_DIAGNOSTIC: {len(before_range)} events")
    
    # Check if they're happening together
    if len(complete_block) > 0 and len(before_range) > 0:
        last_complete = complete_block[-1].get('ts_utc', '')[:19]
        last_before = before_range[-1].get('ts_utc', '')[:19]
        print(f"  Last COMPLETE_BLOCK: {last_complete}")
        print(f"  Last BEFORE_RANGE: {last_before}")

# Show recent events in chronological order for a specific stream
print("\n=== Recent Event Flow (RTY1) ===")
rty1_events = [e for e in all_events if e.get('data', {}).get('payload', {}).get('stream_id') == 'RTY1']
rty1_events.sort(key=lambda e: e.get('ts_utc', ''))
recent_rty1 = [e for e in rty1_events if e.get('ts_utc', '') > '2026-01-21T02:41:00']

print(f"RTY1 events since 02:41:00: {len(recent_rty1)}")
if recent_rty1:
    print("\nLast 20 RTY1 events:")
    for e in recent_rty1[-20:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        event_name = e.get('event', 'N/A')
        print(f"  {ts} | {event_name}")

print("\n" + "="*80)
