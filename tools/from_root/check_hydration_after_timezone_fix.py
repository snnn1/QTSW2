#!/usr/bin/env python3
"""Check hydration status after timezone fix"""
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
        e.get('stream') == 'NQ2' and
        e.get('event') == 'HYDRATION_SUMMARY'):
        hydration_summary.append(e)

# Sort by timestamp
hydration_summary.sort(key=lambda x: x.get('ts_utc', ''))

print("="*80)
print("HYDRATION STATUS FOR NQ2 (After Timezone Fix):")
print("="*80)

if hydration_summary:
    latest = hydration_summary[-1]
    ts = latest.get('ts_utc', 'N/A')[:19]
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"\n  Latest HYDRATION_SUMMARY:")
        print(f"    Timestamp: {ts}")
        print(f"    loaded_bars: {data.get('loaded_bars', 'N/A')}")
        print(f"    expected_bars: {data.get('expected_bars', 'N/A')}")
        completeness_pct = data.get('completeness_pct', 'N/A')
        print(f"    completeness_pct: {completeness_pct}")
        print(f"    range_high: {data.get('range_high', data.get('reconstructed_range_high', 'N/A'))}")
        print(f"    range_low: {data.get('range_low', data.get('reconstructed_range_low', 'N/A'))}")
        
        # Check if range is calculated
        range_high = data.get('range_high', data.get('reconstructed_range_high'))
        range_low = data.get('range_low', data.get('reconstructed_range_low'))
        
        if range_high is not None and range_low is not None:
            print(f"\n  Range Status: CALCULATED")
            print(f"    Range High: {range_high}")
            print(f"    Range Low: {range_low}")
            print(f"    Range Width: {float(range_high) - float(range_low):.2f}")
        else:
            print(f"\n  Range Status: NOT CALCULATED")
        
        # Check completeness
        loaded = data.get('loaded_bars', 0)
        expected = data.get('expected_bars', 0)
        if isinstance(loaded, int) and isinstance(expected, int):
            if loaded == expected:
                print(f"\n  Completeness: PERFECT ({loaded}/{expected} bars)")
            elif loaded > expected * 0.95:
                print(f"\n  Completeness: GOOD ({loaded}/{expected} bars, {completeness_pct}%)")
            elif loaded > 0:
                print(f"\n  Completeness: INCOMPLETE ({loaded}/{expected} bars, {completeness_pct}%)")
            else:
                print(f"\n  Completeness: FAILED (0 bars loaded)")

# Check buffer commits
buffer_committed = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_BUFFER_ADD_COMMITTED'):
        buffer_committed.append(e)

if buffer_committed:
    # Count unique bars in [08:00, 11:00) window
    unique_bars = set()
    bars_by_time = {}
    for e in buffer_committed:
        data = e.get('data', {})
        if isinstance(data, dict):
            bar_utc = data.get('bar_timestamp_utc', '')
            bar_chicago = data.get('bar_timestamp_chicago', '')
            if bar_chicago:
                try:
                    bar_time = datetime.fromisoformat(bar_chicago.replace('Z', '+00:00'))
                    if bar_time.hour >= 8 and (bar_time.hour < 11 or (bar_time.hour == 11 and bar_time.minute == 0)):
                        unique_bars.add(bar_utc)
                        bars_by_time[bar_utc] = bar_chicago
                except:
                    pass
    
    print(f"\n{'='*80}")
    print("BUFFER STATUS:")
    print(f"{'='*80}")
    print(f"  Unique bars committed in [08:00, 11:00): {len(unique_bars)}")
    print(f"  Expected: 180 (3 hours * 60 minutes)")
    
    if len(unique_bars) == 180:
        print(f"  STATUS: CORRECT - Buffer has exactly 180 bars")
    elif len(unique_bars) > 180:
        print(f"  STATUS: WARNING - More bars than expected ({len(unique_bars)} bars)")
    elif len(unique_bars) > 0:
        print(f"  STATUS: INCOMPLETE - Only {len(unique_bars)} bars loaded")
    else:
        print(f"  STATUS: FAILED - No bars in buffer")
    
    # Show time range of bars
    if bars_by_time:
        sorted_times = sorted(bars_by_time.values())
        if sorted_times:
            print(f"\n  Bar time range:")
            print(f"    First bar: {sorted_times[0]}")
            print(f"    Last bar: {sorted_times[-1]}")

# Check for any errors
errors = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') in ['BARSREQUEST_FAILED', 'HYDRATION_COMPLETENESS_CALC_ERROR', 'HYDRATION_RANGE_COMPUTE_ERROR']):
        errors.append(e)

if errors:
    print(f"\n{'='*80}")
    print("ERRORS/WARNINGS:")
    print(f"{'='*80}")
    for e in errors[-5:]:  # Show last 5 errors
        print(f"  {e.get('event', 'N/A')}: {e.get('ts_utc', 'N/A')[:19]}")

print(f"\n{'='*80}")
