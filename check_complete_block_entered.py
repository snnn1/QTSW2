#!/usr/bin/env python3
"""Check for PRE_HYDRATION_COMPLETE_BLOCK_ENTERED events"""
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

block_entered = [e for e in events if 'PRE_HYDRATION_COMPLETE_BLOCK_ENTERED' in e.get('event', '')]
range_diagnostic = [e for e in events if 'PRE_HYDRATION_RANGE_START_DIAGNOSTIC' in e.get('event', '')]
range_diagnostic_error = [e for e in events if 'PRE_HYDRATION_RANGE_START_DIAGNOSTIC_ERROR' in e.get('event', '')]

print("="*80)
print("PRE_HYDRATION_COMPLETE_BLOCK DIAGNOSTICS")
print("="*80)
print(f"\nPRE_HYDRATION_COMPLETE_BLOCK_ENTERED: {len(block_entered)}")
print(f"PRE_HYDRATION_RANGE_START_DIAGNOSTIC: {len(range_diagnostic)}")
print(f"PRE_HYDRATION_RANGE_START_DIAGNOSTIC_ERROR: {len(range_diagnostic_error)}")

if block_entered:
    print("\n=== PRE_HYDRATION_COMPLETE_BLOCK_ENTERED (last 10) ===")
    for e in block_entered[-10:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        payload = e.get('data', {}).get('payload', {})
        stream = payload.get('stream_id', 'N/A')
        log_null = payload.get('log_null', 'N/A')
        time_null = payload.get('time_null', 'N/A')
        print(f"  {ts} | Stream: {stream} | log_null: {log_null} | time_null: {time_null}")

if range_diagnostic:
    print("\n=== PRE_HYDRATION_RANGE_START_DIAGNOSTIC (last 5) ===")
    for e in range_diagnostic[-5:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        payload = e.get('data', {}).get('payload', {})
        stream = payload.get('stream_id', 'N/A')
        range_start = payload.get('range_start_chicago_raw', 'N/A')[:19] if isinstance(payload.get('range_start_chicago_raw'), str) else 'N/A'
        is_default = payload.get('range_start_is_default', 'N/A')
        print(f"  {ts} | Stream: {stream} | RangeStart: {range_start} | Is default: {is_default}")

if range_diagnostic_error:
    print("\n=== PRE_HYDRATION_RANGE_START_DIAGNOSTIC_ERROR (last 5) ===")
    for e in range_diagnostic_error[-5:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        payload = e.get('data', {}).get('payload', {})
        stream = payload.get('stream_id', 'N/A')
        error = payload.get('error', 'N/A')
        print(f"  {ts} | Stream: {stream} | Error: {error}")

print("\n" + "="*80)
