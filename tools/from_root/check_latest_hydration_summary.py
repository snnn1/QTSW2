#!/usr/bin/env python3
"""Check latest HYDRATION_SUMMARY structure"""
import json
from pathlib import Path

log_dir = Path("logs/robot")
events = []

# Read all log files
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

# Find latest HYDRATION_SUMMARY for NQ2 today
today_summary = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'HYDRATION_SUMMARY' and 
        ('NQ2' in str(e) or 'MNQ' in str(e))):
        today_summary.append(e)

if today_summary:
    latest = today_summary[-1]
    print("="*80)
    print("LATEST HYDRATION_SUMMARY STRUCTURE:")
    print("="*80)
    print(f"\nTimestamp: {latest.get('ts_utc', 'N/A')[:19]}")
    print(f"Event: {latest.get('event', 'N/A')}")
    print(f"Stream: {latest.get('stream', 'N/A')}")
    
    # Check data structure - fields are directly in 'data', not 'data.payload'
    data = latest.get('data', {})
    print(f"\nData type: {type(data)}")
    if isinstance(data, dict):
        print(f"Data keys: {list(data.keys())}")
        print(f"\nHYDRATION_SUMMARY VALUES:")
        print(f"  Expected bars: {data.get('expected_bars', 'N/A')}")
        print(f"  Expected full range bars: {data.get('expected_full_range_bars', 'N/A')}")
        print(f"  Loaded bars: {data.get('loaded_bars', data.get('total_bars_in_buffer', 'N/A'))}")
        print(f"  Completeness: {data.get('completeness_pct', 'N/A')}%")
        print(f"  Late start: {data.get('late_start', 'N/A')}")
        print(f"  Missed breakout: {data.get('missed_breakout', 'N/A')}")
        print(f"  Range high: {data.get('reconstructed_range_high', 'N/A')}")
        print(f"  Range low: {data.get('reconstructed_range_low', 'N/A')}")
        print(f"  Historical bars: {data.get('historical_bar_count', 'N/A')}")
        print(f"  Live bars: {data.get('live_bar_count', 'N/A')}")
        print(f"  Deduped bars: {data.get('deduped_bar_count', 'N/A')}")
        print(f"  Total bars in buffer: {data.get('total_bars_in_buffer', 'N/A')}")
        print(f"  Now Chicago: {data.get('now_chicago', 'N/A')}")
        print(f"  Range start Chicago: {data.get('range_start_chicago', 'N/A')}")
        print(f"  Slot time Chicago: {data.get('slot_time_chicago', 'N/A')}")
    else:
        print(f"Data: {str(data)[:500]}")
    
    # Also check top-level fields
    print(f"\nTop-level event keys: {list(latest.keys())}")
    for key in ['instrument', 'slot', 'trading_date', 'total_bars_in_buffer', 'expected_bars', 'late_start']:
        if key in latest:
            print(f"  {key}: {latest[key]}")
else:
    print("No HYDRATION_SUMMARY events found for NQ2 today")

print("\n" + "="*80)
