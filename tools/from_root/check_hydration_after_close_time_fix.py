#!/usr/bin/env python3
"""Check hydration status after CLOSE time fix"""
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

# Find HYDRATION_SUMMARY events
hydration_summaries = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'HYDRATION_SUMMARY'):
        hydration_summaries.append(e)

print("="*80)
print("HYDRATION STATUS AFTER CLOSE TIME FIX:")
print("="*80)

if hydration_summaries:
    latest = hydration_summaries[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        stream = latest.get('stream', 'N/A')
        print(f"\n  Stream: {stream}")
        print(f"  Expected bars: {data.get('expected_bars', 'N/A')}")
        print(f"  Loaded bars: {data.get('loaded_bars', 'N/A')}")
        print(f"  Completeness: {data.get('completeness_pct', 'N/A')}%")
        print(f"  Expected full range bars: {data.get('expected_full_range_bars', 'N/A')}")
        print(f"  Range high: {data.get('range_high', 'N/A')}")
        print(f"  Range low: {data.get('range_low', 'N/A')}")
        print(f"  Late start: {data.get('late_start', 'N/A')}")
        print(f"  Missed breakout: {data.get('missed_breakout', 'N/A')}")
        
        expected = data.get('expected_bars', 0)
        loaded = data.get('loaded_bars', 0)
        if isinstance(expected, (int, float)) and isinstance(loaded, (int, float)):
            if loaded == expected:
                print(f"\n  [SUCCESS] All {loaded} bars loaded!")
            elif loaded < expected:
                print(f"\n  [WARNING] Missing {expected - loaded} bars")
            else:
                print(f"\n  [INFO] More bars than expected (possible duplicates)")
else:
    print(f"\n  No HYDRATION_SUMMARY events found")

print(f"\n{'='*80}")
