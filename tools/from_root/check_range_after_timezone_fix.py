#!/usr/bin/env python3
"""Check range calculation after timezone fixes"""
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

# Find latest HYDRATION_SUMMARY for NQ2
hydration_summary = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'HYDRATION_SUMMARY' and
        e.get('stream') == 'NQ2'):
        hydration_summary.append(e)

if hydration_summary:
    latest = hydration_summary[-1]
    print("="*80)
    print("LATEST HYDRATION_SUMMARY (After Timezone Fixes):")
    print("="*80)
    print(f"  Timestamp: {latest.get('ts_utc', 'N/A')[:19]}")
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"  Reconstructed range high: {data.get('reconstructed_range_high', 'N/A')}")
        print(f"  Reconstructed range low: {data.get('reconstructed_range_low', 'N/A')}")
        print(f"  Range start Chicago: {data.get('range_start_chicago', 'N/A')}")
        print(f"  Slot time Chicago: {data.get('slot_time_chicago', 'N/A')}")
        print(f"  Now Chicago: {data.get('now_chicago', 'N/A')}")
        print(f"  Loaded bars: {data.get('loaded_bars', 'N/A')}")
        print(f"  Expected bars: {data.get('expected_bars', 'N/A')}")
        print(f"  Completeness: {data.get('completeness_pct', 'N/A'):.1f}%" if isinstance(data.get('completeness_pct'), (int, float)) else f"  Completeness: {data.get('completeness_pct', 'N/A')}")
        print(f"  Late start: {data.get('late_start', 'N/A')}")
        print(f"  Missed breakout: {data.get('missed_breakout', 'N/A')}")

# Find latest RANGE_INITIALIZED_FROM_HISTORY
range_init = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'RANGE_INITIALIZED_FROM_HISTORY' and
        e.get('stream') == 'NQ2'):
        range_init.append(e)

if range_init:
    latest = range_init[-1]
    print(f"\n{'='*80}")
    print("LATEST RANGE_INITIALIZED_FROM_HISTORY:")
    print(f"{'='*80}")
    data = latest.get('data', {})
    if isinstance(data, dict):
        payload = data.get('payload', {})
        if isinstance(payload, dict):
            print(f"  Range high: {payload.get('range_high', 'N/A')}")
            print(f"  Range low: {payload.get('range_low', 'N/A')}")
            print(f"  Bar count: {payload.get('bar_count', 'N/A')}")
            print(f"  Range start Chicago: {payload.get('range_start_chicago', 'N/A')}")
            print(f"  Computed up to Chicago: {payload.get('computed_up_to_chicago', 'N/A')}")
            print(f"  Slot time Chicago: {payload.get('slot_time_chicago', 'N/A')}")

# Check timezone conversion in recent events
print(f"\n{'='*80}")
print("TIMEZONE VERIFICATION:")
print(f"{'='*80}")

# Check RANGE_START_INITIALIZED for timezone info
range_start_init = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'RANGE_START_INITIALIZED' and
        e.get('stream') == 'NQ2'):
        range_start_init.append(e)

if range_start_init:
    latest = range_start_init[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        payload = data.get('payload', {})
        if isinstance(payload, dict):
            range_start_chicago = payload.get('range_start_chicago', '')
            if range_start_chicago:
                try:
                    dt = datetime.fromisoformat(range_start_chicago.replace('Z', '+00:00'))
                    offset_hours = dt.utcoffset().total_seconds() / 3600
                    print(f"  Range start Chicago: {range_start_chicago}")
                    print(f"  Offset: UTC{offset_hours:+.0f}:00")
                    if offset_hours in [-6, -5]:
                        print(f"  ✓ Chicago timezone confirmed")
                    else:
                        print(f"  ⚠️  WARNING: Unexpected offset!")
                except:
                    pass

# Check for any bar timestamps to verify conversion
print(f"\n{'='*80}")
print("COMPARISON:")
print(f"{'='*80}")
if hydration_summary:
    latest = hydration_summary[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        range_high = data.get('reconstructed_range_high')
        if isinstance(range_high, (int, float)):
            print(f"  System range high: {range_high}")
            print(f"  Your expected range high: 25903")
            diff = 25903 - range_high
            print(f"  Difference: {diff:.2f} points")
            if abs(diff) < 1:
                print(f"  ✓ Range matches expected value!")
            elif abs(diff) < 50:
                print(f"  ⚠️  Small difference - may be timing (range calculated before all bars)")
            else:
                print(f"  ⚠️  Significant difference - check if:")
                print(f"     1. Range was calculated before slot_time (bars still arriving)")
                print(f"     2. Bars with higher prices are outside time window")
                print(f"     3. Bar timestamps are being filtered correctly")

print(f"\n{'='*80}")
