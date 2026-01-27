#!/usr/bin/env python3
"""Check RANGE_COMPUTE_FAILED errors"""
import json
from pathlib import Path

log_dir = Path("logs/robot")
events = []

for log_file in log_dir.glob("robot_*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                if line.strip():
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except:
        pass

print("="*80)
print("RANGE_COMPUTE_FAILED ERRORS:")
print("="*80)

range_failed = [e for e in events 
               if e.get('ts_utc', '').startswith('2026-01-26') and
               e.get('event') == 'RANGE_COMPUTE_FAILED']

range_failed.sort(key=lambda x: x.get('ts_utc', ''))

for e in range_failed:
    stream = e.get('stream', 'N/A')
    data = e.get('data', {})
    ts = e.get('ts_utc', '')[:19]
    
    print(f"\n  Stream: {stream}")
    print(f"  Time: {ts}")
    print(f"  Error: {data.get('error', 'N/A')}")
    print(f"  Reason: {data.get('reason', 'N/A')}")
    if data.get('bar_count'):
        print(f"  Bar count: {data.get('bar_count')}")

print(f"\n{'='*80}")
print("CHECKING WHY BARS AREN'T LOADED:")
print(f"{'='*80}")

# Check for PRE_HYDRATION_BARS_LOADED events
bars_loaded_events = [e for e in events 
                     if e.get('ts_utc', '').startswith('2026-01-26') and
                     e.get('event') == 'PRE_HYDRATION_BARS_LOADED']

print(f"\n  PRE_HYDRATION_BARS_LOADED events: {len(bars_loaded_events)}")
for e in bars_loaded_events:
    data = e.get('data', {})
    instrument = data.get('instrument', 'N/A')
    bar_count = data.get('bar_count', 0)
    streams_fed = data.get('streams_fed', 0)
    ts = e.get('ts_utc', '')[:19]
    print(f"    {ts} - {instrument}: {bar_count} bars, {streams_fed} streams fed")

# Check for PRE_HYDRATION_BARS_SKIPPED_STREAM_STATE
skipped_state = [e for e in events 
                if e.get('ts_utc', '').startswith('2026-01-26') and
                e.get('event') == 'PRE_HYDRATION_BARS_SKIPPED_STREAM_STATE']

print(f"\n  PRE_HYDRATION_BARS_SKIPPED_STREAM_STATE: {len(skipped_state)}")
for e in skipped_state[:10]:  # Show first 10
    data = e.get('data', {})
    instrument = data.get('instrument', 'N/A')
    stream_id = data.get('stream_id', 'N/A')
    stream_state = data.get('stream_state', 'N/A')
    ts = e.get('ts_utc', '')[:19]
    print(f"    {ts} - {instrument} -> {stream_id}: State={stream_state}")

print(f"\n{'='*80}")
