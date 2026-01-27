#!/usr/bin/env python3
"""Check if HYDRATION_SUMMARY fix is working"""
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

# Find HYDRATION_SUMMARY events for NQ2
hydration_summary = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'HYDRATION_SUMMARY'):
        hydration_summary.append(e)

# Sort by timestamp
hydration_summary.sort(key=lambda x: x.get('ts_utc', ''))

print("="*80)
print("HYDRATION_SUMMARY EVENTS FOR NQ2:")
print("="*80)

if hydration_summary:
    print(f"\n  Total HYDRATION_SUMMARY events: {len(hydration_summary)}")
    print(f"\n  Latest 5 summaries:")
    
    for i, e in enumerate(hydration_summary[-5:], 1):
        ts = e.get('ts_utc', 'N/A')[:19]
        data = e.get('data', {})
        if isinstance(data, dict):
            loaded_bars = data.get('loaded_bars', 'N/A')
            expected_bars = data.get('expected_bars', 'N/A')
            completeness_pct = data.get('completeness_pct', 'N/A')
            range_high = data.get('range_high', data.get('reconstructed_range_high', 'N/A'))
            range_low = data.get('range_low', data.get('reconstructed_range_low', 'N/A'))
            
            print(f"\n  Summary #{len(hydration_summary) - 5 + i}:")
            print(f"    Timestamp: {ts}")
            print(f"    loaded_bars: {loaded_bars}")
            print(f"    expected_bars: {expected_bars}")
            print(f"    completeness_pct: {completeness_pct}")
            print(f"    range_high: {range_high}")
            print(f"    range_low: {range_low}")
    
    # Check latest summary
    latest = hydration_summary[-1]
    latest_data = latest.get('data', {})
    if isinstance(latest_data, dict):
        latest_loaded = latest_data.get('loaded_bars', 'N/A')
        latest_expected = latest_data.get('expected_bars', 'N/A')
        
        print(f"\n{'='*80}")
        print("LATEST HYDRATION_SUMMARY:")
        print(f"{'='*80}")
        print(f"  loaded_bars: {latest_loaded}")
        print(f"  expected_bars: {latest_expected}")
        
        if latest_loaded == 180:
            print(f"  STATUS: CORRECT! Buffer has 180 bars")
        elif latest_loaded == 0:
            print(f"  STATUS: STILL BROKEN - showing 0 bars")
        else:
            print(f"  STATUS: Showing {latest_loaded} bars (expected 180)")

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
    for e in buffer_committed:
        data = e.get('data', {})
        if isinstance(data, dict):
            bar_chicago = data.get('bar_timestamp_chicago', '')
            if bar_chicago:
                try:
                    bar_time = datetime.fromisoformat(bar_chicago.replace('Z', '+00:00'))
                    if bar_time.hour >= 8 and (bar_time.hour < 11 or (bar_time.hour == 11 and bar_time.minute == 0)):
                        unique_bars.add(data.get('bar_timestamp_utc', ''))
                except:
                    pass
    
    print(f"\n{'='*80}")
    print("BUFFER STATUS:")
    print(f"{'='*80}")
    print(f"  Unique bars committed in [08:00, 11:00): {len(unique_bars)}")
    print(f"  Expected: 180")
    
    if len(unique_bars) == 180:
        print(f"  STATUS: Buffer has correct number of bars")
    else:
        print(f"  STATUS: Buffer has {len(unique_bars)} bars (expected 180)")

print(f"\n{'='*80}")
