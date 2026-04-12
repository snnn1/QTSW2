#!/usr/bin/env python3
"""Compute range from current bars in buffer"""
import json
from pathlib import Path
from datetime import datetime, timedelta

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

# Get all committed bars for NQ2
nq2_bars = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_BUFFER_ADD_COMMITTED'):
        data = e.get('data', {})
        if isinstance(data, dict):
            bar_chicago = data.get('bar_timestamp_chicago', '')
            bar_utc = data.get('bar_timestamp_utc', '')
            high = data.get('high', None)
            low = data.get('low', None)
            
            if bar_chicago and high is not None and low is not None:
                try:
                    bar_time = datetime.fromisoformat(bar_chicago.replace('Z', '+00:00'))
                    # Only include bars in range window [08:00, 11:00)
                    if bar_time.hour >= 8 and (bar_time.hour < 11 or (bar_time.hour == 11 and bar_time.minute == 0)):
                        nq2_bars.append({
                            'time': bar_time,
                            'high': float(high),
                            'low': float(low),
                            'open': float(data.get('open', 0)),
                            'close': float(data.get('close', 0))
                        })
                except:
                    pass

# Sort by time
nq2_bars.sort(key=lambda x: x['time'])

# Get range boundaries from latest HYDRATION_SUMMARY
range_start = None
slot_time = None
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'HYDRATION_SUMMARY'):
        data = e.get('data', {})
        if isinstance(data, dict):
            range_start_str = data.get('range_start_chicago', '')
            slot_time_str = data.get('slot_time_chicago', '')
            if range_start_str:
                try:
                    range_start = datetime.fromisoformat(range_start_str.replace('Z', '+00:00'))
                except:
                    pass
            if slot_time_str:
                try:
                    slot_time = datetime.fromisoformat(slot_time_str.replace('Z', '+00:00'))
                except:
                    pass
        break

print("="*80)
print("RANGE COMPUTATION FROM CURRENT BARS:")
print("="*80)

if nq2_bars:
    # Filter bars in range window [range_start, slot_time)
    range_bars = []
    for bar in nq2_bars:
        bar_time = bar['time']
        if range_start and slot_time:
            if bar_time >= range_start and bar_time < slot_time:
                range_bars.append(bar)
        else:
            # Fallback: use time-based filtering
            if bar_time.hour >= 8 and (bar_time.hour < 11 or (bar_time.hour == 11 and bar_time.minute == 0)):
                range_bars.append(bar)
    
    if range_bars:
        # Compute range
        range_high = max(bar['high'] for bar in range_bars)
        range_low = min(bar['low'] for bar in range_bars)
        spread = range_high - range_low
        
        print(f"\n  Stream: NQ2")
        print(f"  Bars in range window: {len(range_bars)}")
        print(f"  Range window: [{range_start.strftime('%H:%M') if range_start else '08:00'}, {slot_time.strftime('%H:%M') if slot_time else '11:00'})")
        
        print(f"\n  COMPUTED RANGE (NOW):")
        print(f"    Range High: {range_high}")
        print(f"    Range Low: {range_low}")
        print(f"    Spread: {spread}")
        
        # Find which bars contributed to high/low
        high_bar = next((b for b in range_bars if b['high'] == range_high), None)
        low_bar = next((b for b in range_bars if b['low'] == range_low), None)
        
        if high_bar:
            print(f"\n  Range High from bar at: {high_bar['time'].strftime('%H:%M:%S')}")
        if low_bar:
            print(f"\n  Range Low from bar at: {low_bar['time'].strftime('%H:%M:%S')}")
        
        # Show time range of bars
        if range_bars:
            first_bar_time = range_bars[0]['time']
            last_bar_time = range_bars[-1]['time']
            print(f"\n  Bar time range:")
            print(f"    First bar: {first_bar_time.strftime('%H:%M:%S')}")
            print(f"    Last bar: {last_bar_time.strftime('%H:%M:%S')}")
        
        # Compare with latest RANGE_INITIALIZED_FROM_HISTORY
        latest_range = None
        for e in events:
            if (e.get('ts_utc', '').startswith('2026-01-26') and 
                e.get('stream') == 'NQ2' and
                e.get('event') == 'RANGE_INITIALIZED_FROM_HISTORY'):
                latest_range = e
                break
        
        if latest_range:
            range_data = latest_range.get('data', {})
            if isinstance(range_data, dict):
                old_high = range_data.get('range_high')
                old_low = range_data.get('range_low')
                if old_high is not None and old_low is not None:
                    print(f"\n  COMPARISON WITH LAST COMPUTED RANGE:")
                    print(f"    Last computed High: {old_high}")
                    print(f"    Current High: {range_high}")
                    print(f"    Difference: {range_high - float(old_high):.2f}")
                    print(f"    Last computed Low: {old_low}")
                    print(f"    Current Low: {range_low}")
                    print(f"    Difference: {range_low - float(old_low):.2f}")
    else:
        print(f"\n  No bars found in range window")
else:
    print(f"\n  No bars found in buffer")

print(f"\n{'='*80}")
