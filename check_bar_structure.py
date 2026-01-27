#!/usr/bin/env python3
"""Check structure of BAR_BUFFER_ADD_COMMITTED events"""
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

# Find NQ2 BAR_BUFFER_ADD_COMMITTED events
nq2_bars = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_BUFFER_ADD_COMMITTED'):
        nq2_bars.append(e)

print("="*80)
print("BAR_BUFFER_ADD_COMMITTED STRUCTURE:")
print("="*80)

if nq2_bars:
    # Show first few bars
    print(f"\n  Found {len(nq2_bars)} committed bars")
    print(f"\n  Sample bar (first):")
    sample = nq2_bars[0]
    print(f"    Event: {sample.get('event')}")
    print(f"    Stream: {sample.get('stream')}")
    print(f"    Timestamp: {sample.get('ts_utc', 'N/A')[:19]}")
    
    data = sample.get('data', {})
    if isinstance(data, dict):
        print(f"\n    Data fields:")
        for key, value in sorted(data.items()):
            print(f"      {key}: {value}")
    
    # Now compute range from all bars
    print(f"\n{'='*80}")
    print("COMPUTING RANGE FROM ALL COMMITTED BARS:")
    print(f"{'='*80}")
    
    bars_with_prices = []
    for bar_event in nq2_bars:
        data = bar_event.get('data', {})
        if isinstance(data, dict):
            # Try different possible field names
            bar_time_str = (data.get('bar_timestamp_chicago') or 
                           data.get('bar_time_chicago') or 
                           data.get('timestamp_chicago') or '')
            high = data.get('high') or data.get('bar_high')
            low = data.get('low') or data.get('bar_low')
            
            if bar_time_str and high is not None and low is not None:
                try:
                    from datetime import datetime
                    bar_time = datetime.fromisoformat(bar_time_str.replace('Z', '+00:00'))
                    bars_with_prices.append({
                        'time': bar_time,
                        'high': float(high),
                        'low': float(low)
                    })
                except Exception as ex:
                    pass
    
    if bars_with_prices:
        # Filter to range window [08:00, 11:00)
        range_bars = [b for b in bars_with_prices 
                     if b['time'].hour >= 8 and (b['time'].hour < 11 or (b['time'].hour == 11 and b['time'].minute == 0))]
        
        if range_bars:
            range_high = max(b['high'] for b in range_bars)
            range_low = min(b['low'] for b in range_bars)
            spread = range_high - range_low
            
            print(f"\n  Bars in range window: {len(range_bars)}")
            print(f"  Range High: {range_high}")
            print(f"  Range Low: {range_low}")
            print(f"  Spread: {spread}")
            
            # Find contributing bars
            high_bar = next((b for b in range_bars if b['high'] == range_high), None)
            low_bar = next((b for b in range_bars if b['low'] == range_low), None)
            
            if high_bar:
                print(f"  Range High from: {high_bar['time'].strftime('%H:%M:%S')}")
            if low_bar:
                print(f"  Range Low from: {low_bar['time'].strftime('%H:%M:%S')}")
        else:
            print(f"\n  No bars in range window [08:00, 11:00)")
    else:
        print(f"\n  Could not extract price data from bars")
        print(f"  Sample data keys: {list(data.keys()) if isinstance(data, dict) else 'N/A'}")
else:
    print(f"\n  No BAR_BUFFER_ADD_COMMITTED events found for NQ2")

print(f"\n{'='*80}")
