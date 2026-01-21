#!/usr/bin/env python3
"""Check pre-hydration status and execution mode"""
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

# Sort by timestamp
events.sort(key=lambda e: e.get('ts_utc', ''))

print("="*80)
print("PRE-HYDRATION STATUS CHECK")
print("="*80)

# Check execution mode from TICK_CALLED events
tick_called = [e for e in events if 'TICK_CALLED' in e.get('event', '')]
if tick_called:
    latest = tick_called[-1]
    payload = latest.get('data', {}).get('payload', {})
    exec_mode = payload.get('execution_mode', 'N/A')
    print(f"\nExecution Mode: {exec_mode}")
    print(f"  From TICK_CALLED event at: {latest.get('ts_utc', 'N/A')[:19]}")

# Check PRE_HYDRATION_HANDLER_TRACE events
handler_trace = [e for e in events if 'PRE_HYDRATION_HANDLER_TRACE' in e.get('event', '')]
print(f"\nPRE_HYDRATION_HANDLER_TRACE events: {len(handler_trace)}")
if handler_trace:
    latest = handler_trace[-1]
    payload = latest.get('data', {}).get('payload', {})
    print(f"  Latest: {latest.get('ts_utc', 'N/A')[:19]}")
    print(f"  Stream: {payload.get('stream_id', 'N/A')}")
    print(f"  Bar count: {payload.get('bar_count', 'N/A')}")

# Check PRE_HYDRATION_RANGE_START_DIAGNOSTIC events
range_diagnostic = [e for e in events if 'PRE_HYDRATION_RANGE_START_DIAGNOSTIC' in e.get('event', '')]
print(f"\nPRE_HYDRATION_RANGE_START_DIAGNOSTIC events: {len(range_diagnostic)}")
if range_diagnostic:
    latest = range_diagnostic[-1]
    payload = latest.get('data', {}).get('payload', {})
    print(f"  Latest: {latest.get('ts_utc', 'N/A')[:19]}")
    print(f"  Stream: {payload.get('stream_id', 'N/A')}")
    print(f"  RangeStart is default: {payload.get('range_start_is_default', 'N/A')}")
    print(f"  RangeStart year: {payload.get('range_start_year', 'N/A')}")
else:
    print("  [WARN] No PRE_HYDRATION_RANGE_START_DIAGNOSTIC events found!")
    print("    -> This means _preHydrationComplete is false")
    print("    -> OR execution mode is DRYRUN and file-based pre-hydration hasn't completed")

# Check for PRE_HYDRATION errors
pre_hyd_errors = [e for e in events if 'PRE_HYDRATION' in e.get('event', '') and 'ERROR' in e.get('event', '')]
print(f"\nPRE_HYDRATION error events: {len(pre_hyd_errors)}")
if pre_hyd_errors:
    for e in pre_hyd_errors[-5:]:
        payload = e.get('data', {}).get('payload', {})
        print(f"  {e.get('ts_utc', 'N/A')[:19]} | {e.get('event', 'N/A')}")
        print(f"    Stream: {payload.get('stream_id', 'N/A')}")
        print(f"    Error: {payload.get('error', 'N/A')}")

print("\n" + "="*80)
