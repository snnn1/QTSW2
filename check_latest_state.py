#!/usr/bin/env python3
"""Check the latest state of NQ2"""
import json
from pathlib import Path
from datetime import datetime

log_dir = Path("logs/robot")
events = []

# Read all robot log files
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
    except Exception as e:
        pass

# Get latest events for NQ2
nq2_events = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2'):
        nq2_events.append(e)

# Sort by timestamp
nq2_events.sort(key=lambda x: x.get('ts_utc', ''))

print("="*80)
print("LATEST NQ2 EVENTS (last 20):")
print("="*80)

for e in nq2_events[-20:]:
    event_name = e.get('event', 'N/A')
    ts = e.get('ts_utc', 'N/A')[:19]
    state = e.get('state', 'N/A')
    print(f"  {ts} | {event_name:30s} | State: {state}")

# Check latest HYDRATION_SUMMARY
latest_summary = None
for e in reversed(nq2_events):
    if e.get('event') == 'HYDRATION_SUMMARY':
        latest_summary = e
        break

if latest_summary:
    print(f"\n{'='*80}")
    print("LATEST HYDRATION_SUMMARY:")
    print(f"{'='*80}")
    print(f"  Timestamp: {latest_summary.get('ts_utc', 'N/A')[:19]}")
    print(f"  State: {latest_summary.get('state', 'N/A')}")
    data = latest_summary.get('data', {})
    if isinstance(data, dict):
        print(f"  loaded_bars: {data.get('loaded_bars', 'N/A')}")
        print(f"  expected_bars: {data.get('expected_bars', 'N/A')}")
        print(f"  completeness_pct: {data.get('completeness_pct', 'N/A')}")
        print(f"  range_high: {data.get('range_high', data.get('reconstructed_range_high', 'N/A'))}")
        print(f"  range_low: {data.get('range_low', data.get('reconstructed_range_low', 'N/A'))}")

# Check latest buffer commit
latest_commit = None
for e in reversed(nq2_events):
    if e.get('event') == 'BAR_BUFFER_ADD_COMMITTED':
        latest_commit = e
        break

if latest_commit:
    print(f"\n{'='*80}")
    print("LATEST BUFFER COMMIT:")
    print(f"{'='*80}")
    print(f"  Timestamp: {latest_commit.get('ts_utc', 'N/A')[:19]}")
    data = latest_commit.get('data', {})
    if isinstance(data, dict):
        print(f"  Buffer count after commit: {data.get('new_buffer_count', 'N/A')}")
        print(f"  Bar timestamp: {data.get('bar_timestamp_chicago', 'N/A')}")

print(f"\n{'='*80}")
