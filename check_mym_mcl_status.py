#!/usr/bin/env python3
"""Check MYM and MCL BarsRequest details"""
import json
from pathlib import Path
from collections import defaultdict

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

print("="*80)
print("MYM BARSREQUEST DETAILS:")
print("="*80)

# Get latest MYM BarsRequest events
mym_events = []
for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        data = e.get('data', {})
        if isinstance(data, dict):
            instrument = data.get('instrument', '')
            if instrument == 'MYM':
                mym_events.append(e)

# Sort by timestamp
mym_events.sort(key=lambda x: x.get('ts_utc', ''))

# Show latest executed
executed = [e for e in mym_events if e.get('event') == 'BARSREQUEST_EXECUTED']
if executed:
    latest = executed[-1]
    data = latest.get('data', {})
    print(f"\n  Latest BARSREQUEST_EXECUTED:")
    print(f"    Bars returned: {data.get('bars_returned', 'N/A')}")
    print(f"    First bar UTC: {data.get('first_bar_utc', 'N/A')}")
    print(f"    Last bar UTC: {data.get('last_bar_utc', 'N/A')}")
    print(f"    Time: {latest.get('ts_utc', 'N/A')[:19]}")

# Check if bars were loaded
mym_loaded = [e for e in events 
             if e.get('ts_utc', '').startswith('2026-01-26') and
             e.get('event') == 'PRE_HYDRATION_BARS_LOADED']
mym_loaded_filtered = [e for e in mym_loaded 
                      if e.get('data', {}).get('instrument') == 'MYM']

print(f"\n  PRE_HYDRATION_BARS_LOADED events for MYM: {len(mym_loaded_filtered)}")
if mym_loaded_filtered:
    latest_loaded = mym_loaded_filtered[-1]
    data = latest_loaded.get('data', {})
    print(f"    Bars loaded: {data.get('bar_count', 'N/A')}")
    print(f"    Streams fed: {data.get('streams_fed', 'N/A')}")

# Check YM streams
print(f"\n{'='*80}")
print("YM STREAMS STATUS:")
print(f"{'='*80}")

for stream in ['YM1', 'YM2']:
    stream_events = [e for e in events 
                    if e.get('ts_utc', '').startswith('2026-01-26') and 
                    e.get('stream') == stream]
    
    committed = len([e for e in stream_events if e.get('event') == 'BAR_BUFFER_ADD_COMMITTED'])
    attempts = len([e for e in stream_events if e.get('event') == 'BAR_BUFFER_ADD_ATTEMPT'])
    rejected = len([e for e in stream_events if e.get('event') == 'BAR_BUFFER_REJECTED'])
    
    print(f"\n  {stream}:")
    print(f"    BAR_BUFFER_ADD_ATTEMPT: {attempts}")
    print(f"    BAR_BUFFER_REJECTED: {rejected}")
    print(f"    BAR_BUFFER_ADD_COMMITTED: {committed}")
    
    # Check hydration summary
    hydration = [e for e in stream_events if e.get('event') == 'HYDRATION_SUMMARY']
    if hydration:
        latest_h = hydration[-1]
        h_data = latest_h.get('data', {})
        print(f"    HYDRATION_SUMMARY:")
        print(f"      Loaded bars: {h_data.get('loaded_bars', 'N/A')}")
        print(f"      Expected bars: {h_data.get('expected_bars', 'N/A')}")
        print(f"      Completeness: {h_data.get('completeness_pct', 'N/A')}%")

print(f"\n{'='*80}")
print("MCL BARSREQUEST DETAILS:")
print(f"{'='*80}")

# Get latest MCL BarsRequest events
mcl_events = []
for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        data = e.get('data', {})
        if isinstance(data, dict):
            instrument = data.get('instrument', '')
            if instrument == 'MCL':
                mcl_events.append(e)

# Show latest skipped/failed
skipped = [e for e in mcl_events if e.get('event') == 'BARSREQUEST_SKIPPED']
failed = [e for e in mcl_events if e.get('event') == 'BARSREQUEST_FAILED']

if skipped:
    latest_skip = skipped[-1]
    data = latest_skip.get('data', {})
    print(f"\n  Latest BARSREQUEST_SKIPPED:")
    print(f"    Reason: {data.get('reason', 'N/A')}")
    print(f"    Time: {latest_skip.get('ts_utc', 'N/A')[:19]}")

if failed:
    latest_fail = failed[-1]
    data = latest_fail.get('data', {})
    print(f"\n  Latest BARSREQUEST_FAILED:")
    print(f"    Reason: {data.get('reason', 'N/A')}")
    print(f"    Error: {data.get('error', 'N/A')}")
    print(f"    Time: {latest_fail.get('ts_utc', 'N/A')[:19]}")

# Check CL streams
print(f"\n{'='*80}")
print("CL STREAMS STATUS:")
print(f"{'='*80}")

for stream in ['CL1', 'CL2']:
    stream_events = [e for e in events 
                    if e.get('ts_utc', '').startswith('2026-01-26') and 
                    e.get('stream') == stream]
    
    committed = len([e for e in stream_events if e.get('event') == 'BAR_BUFFER_ADD_COMMITTED'])
    attempts = len([e for e in stream_events if e.get('event') == 'BAR_BUFFER_ADD_ATTEMPT'])
    
    print(f"\n  {stream}:")
    print(f"    BAR_BUFFER_ADD_ATTEMPT: {attempts}")
    print(f"    BAR_BUFFER_ADD_COMMITTED: {committed}")

print(f"\n{'='*80}")
