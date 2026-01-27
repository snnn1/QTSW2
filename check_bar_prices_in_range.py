#!/usr/bin/env python3
"""Check actual bar prices in the range window"""
import json
from pathlib import Path
from datetime import datetime

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

# Find BAR_ADMISSION_PROOF events for NQ2 with actual prices
bar_admissions = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        'BAR_ADMISSION' in e.get('event', '')):
        bar_admissions.append(e)

if bar_admissions:
    print("="*80)
    print("BAR PRICES IN NQ2 STREAM (last 20 bars):")
    print("="*80)
    
    # Sort by timestamp
    bar_admissions.sort(key=lambda x: x.get('ts_utc', ''))
    
    highs = []
    lows = []
    
    for e in bar_admissions[-20:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        data = e.get('data', {})
        if isinstance(data, dict):
            payload = data.get('payload', {})
            if isinstance(payload, dict):
                high = payload.get('high')
                low = payload.get('low')
                close = payload.get('close')
                if high is not None:
                    highs.append(float(high))
                if low is not None:
                    lows.append(float(low))
                print(f"  {ts} | High: {high} | Low: {low} | Close: {close}")
    
    if highs:
        print(f"\n{'='*80}")
        print("STATISTICS:")
        print(f"{'='*80}")
        print(f"  Max High in last 20 bars: {max(highs):.2f}")
        print(f"  Min Low in last 20 bars: {min(lows):.2f}")
        print(f"  System calculated range high: 25742.25")
        print(f"  Your MNQ range high: 25903")
        print(f"  Difference: {25903 - 25742.25:.2f} points")
        
        if max(highs) > 25742.25:
            print(f"\n  ⚠️  WARNING: Found bars with higher prices than calculated range!")
            print(f"      This suggests the range calculation may be missing some bars")
        else:
            print(f"\n  ✓  All bar prices are within calculated range")
            print(f"      The difference may be due to:")
            print(f"      1. Price moved higher after range calculation (10:23:14)")
            print(f"      2. Range calculated from different time window")
            print(f"      3. Different data source (your 25903 vs system bars)")

print(f"\n{'='*80}")
