#!/usr/bin/env python3
"""Check for PRE_HYDRATION_BEFORE_RANGE_DIAGNOSTIC events"""
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
                        if ts > '2026-01-21T02:38:00':
                            events.append(event)
                    except:
                        pass
    except:
        pass

events.sort(key=lambda e: e.get('ts_utc', ''))

before_range = [e for e in events if 'PRE_HYDRATION_BEFORE_RANGE_DIAGNOSTIC' in e.get('event', '')]
range_diagnostic = [e for e in events if 'PRE_HYDRATION_RANGE_START_DIAGNOSTIC' in e.get('event', '')]
range_error = [e for e in events if 'PRE_HYDRATION_RANGE_START_DIAGNOSTIC_ERROR' in e.get('event', '')]
null_check_failed = [e for e in events if 'PRE_HYDRATION_RANGE_START_DIAGNOSTIC_NULL_CHECK_FAILED' in e.get('event', '')]

print("="*80)
print("PRE_HYDRATION RANGE DIAGNOSTIC FLOW CHECK")
print("="*80)
print(f"\nEvents since 02:38:00: {len(events)}")
print(f"\nPRE_HYDRATION_BEFORE_RANGE_DIAGNOSTIC: {len(before_range)}")
print(f"PRE_HYDRATION_RANGE_START_DIAGNOSTIC: {len(range_diagnostic)}")
print(f"PRE_HYDRATION_RANGE_START_DIAGNOSTIC_ERROR: {len(range_error)}")
print(f"PRE_HYDRATION_RANGE_START_DIAGNOSTIC_NULL_CHECK_FAILED: {len(null_check_failed)}")

if before_range:
    print("\n=== PRE_HYDRATION_BEFORE_RANGE_DIAGNOSTIC (last 10) ===")
    for e in before_range[-10:]:
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

if range_error:
    print("\n=== PRE_HYDRATION_RANGE_START_DIAGNOSTIC_ERROR (last 5) ===")
    for e in range_error[-5:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        payload = e.get('data', {}).get('payload', {})
        stream = payload.get('stream_id', 'N/A')
        error = payload.get('error', 'N/A')
        ex_type = payload.get('exception_type', 'N/A')
        print(f"  {ts} | Stream: {stream}")
        print(f"    Error: {error}")
        print(f"    Exception Type: {ex_type}")

if null_check_failed:
    print("\n=== PRE_HYDRATION_RANGE_START_DIAGNOSTIC_NULL_CHECK_FAILED (last 5) ===")
    for e in null_check_failed[-5:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        payload = e.get('data', {}).get('payload', {})
        stream = payload.get('stream_id', 'N/A')
        log_null = payload.get('log_null', 'N/A')
        time_null = payload.get('time_null', 'N/A')
        print(f"  {ts} | Stream: {stream} | log_null: {log_null} | time_null: {time_null}")

print("\n" + "="*80)
