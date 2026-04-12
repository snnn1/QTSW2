#!/usr/bin/env python3
"""Check if bars in the correct window [08:00, 11:00) are being requested"""
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

# Find BAR_ADMISSION_PROOF events for NQ2
bar_proof = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_ADMISSION_PROOF'):
        bar_proof.append(e)

if bar_proof:
    print("="*80)
    print(f"BAR_ADMISSION_PROOF FOR NQ2 (Found {len(bar_proof)}):")
    print("="*80)
    
    # Filter bars in the 08:00-11:00 window
    bars_in_window = []
    bars_outside_window = []
    
    for e in bar_proof:
        data = e.get('data', {})
        if isinstance(data, dict):
            bar_time_str = data.get('bar_time_chicago', '')
            if bar_time_str:
                try:
                    # Parse the bar time
                    bar_time = datetime.fromisoformat(bar_time_str.replace('Z', '+00:00'))
                    bar_hour = bar_time.hour
                    bar_minute = bar_time.minute
                    
                    # Check if in [08:00, 11:00)
                    if bar_hour == 8 or (bar_hour == 9) or (bar_hour == 10) or (bar_hour == 11 and bar_minute == 0):
                        bars_in_window.append((bar_time_str, data.get('comparison_result', False)))
                    else:
                        bars_outside_window.append((bar_time_str, data.get('comparison_result', False)))
                except:
                    pass
    
    print(f"\n  Bars in window [08:00, 11:00): {len(bars_in_window)}")
    print(f"  Bars outside window: {len(bars_outside_window)}")
    
    if bars_in_window:
        print(f"\n  First 10 bars IN window:")
        for bar_time, result in bars_in_window[:10]:
            print(f"    {bar_time} | Accepted: {result}")
        
        accepted_in_window = sum(1 for _, result in bars_in_window if result == True)
        print(f"\n  Accepted bars in window: {accepted_in_window}/{len(bars_in_window)}")
    
    if bars_outside_window:
        print(f"\n  Sample bars OUTSIDE window:")
        for bar_time, result in bars_outside_window[:5]:
            print(f"    {bar_time} | Accepted: {result}")

# Check latest HYDRATION_SUMMARY
hydration = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'HYDRATION_SUMMARY' and
        e.get('stream') == 'NQ2'):
        hydration.append(e)

if hydration:
    latest = hydration[-1]
    print(f"\n{'='*80}")
    print("LATEST HYDRATION_SUMMARY:")
    print(f"{'='*80}")
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"  Loaded bars: {data.get('loaded_bars', 'N/A')}")
        print(f"  Total bars in buffer: {data.get('total_bars_in_buffer', 'N/A')}")
        print(f"  Range high: {data.get('reconstructed_range_high', 'N/A')}")
        print(f"  Range low: {data.get('reconstructed_range_low', 'N/A')}")

print(f"\n{'='*80}")
