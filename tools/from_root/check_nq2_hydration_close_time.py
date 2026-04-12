#!/usr/bin/env python3
"""Check NQ2 hydration with CLOSE time fix"""
import json
from pathlib import Path

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

# Find NQ2 events
nq2_hydration = []
nq2_close_boundaries = []
nq2_close_verified = []
nq2_bars_committed = []

for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        stream = e.get('stream', '')
        event_type = e.get('event', '')
        
        if stream == 'NQ2':
            if event_type == 'HYDRATION_SUMMARY':
                nq2_hydration.append(e)
            elif event_type == 'BARSREQUEST_CLOSE_TIME_BOUNDARIES':
                nq2_close_boundaries.append(e)
            elif event_type == 'BARSREQUEST_CLOSE_TIME_VERIFIED':
                nq2_close_verified.append(e)
            elif event_type == 'BAR_BUFFER_ADD_COMMITTED':
                nq2_bars_committed.append(e)

print("="*80)
print("NQ2 HYDRATION WITH CLOSE TIME FIX:")
print("="*80)

if nq2_close_boundaries:
    latest = nq2_close_boundaries[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"\n  BARSREQUEST_CLOSE_TIME_BOUNDARIES:")
        print(f"    Desired OPEN: [{data.get('desired_open_start_chicago', 'N/A')}, {data.get('desired_open_end_chicago', 'N/A')})")
        print(f"    Request CLOSE: [{data.get('request_close_start_chicago', 'N/A')}, {data.get('request_close_end_chicago', 'N/A')})")

if nq2_close_verified:
    latest = nq2_close_verified[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"\n  BARSREQUEST_CLOSE_TIME_VERIFIED:")
        print(f"    Bars returned: {data.get('bars_returned', 'N/A')}")
        print(f"    Bars filtered out: {data.get('bars_filtered_out', 'N/A')}")
        print(f"    First close UTC: {data.get('first_close_time_utc', 'N/A')}")
        print(f"    Last close UTC: {data.get('last_close_time_utc', 'N/A')}")
        print(f"    Assertion passed: {data.get('assertion_passed', 'N/A')}")

if nq2_hydration:
    latest = nq2_hydration[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"\n  HYDRATION_SUMMARY:")
        print(f"    Expected bars: {data.get('expected_bars', 'N/A')}")
        print(f"    Loaded bars: {data.get('loaded_bars', 'N/A')}")
        print(f"    Completeness: {data.get('completeness_pct', 'N/A')}%")
        print(f"    Expected full range: {data.get('expected_full_range_bars', 'N/A')}")
        print(f"    Range high: {data.get('range_high', 'N/A')}")
        print(f"    Range low: {data.get('range_low', 'N/A')}")

print(f"\n  BAR_BUFFER_ADD_COMMITTED count: {len(nq2_bars_committed)}")

print(f"\n{'='*80}")
