#!/usr/bin/env python3
"""Check which bar is missing"""
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

# Get all committed bars for NQ2 in [08:00, 11:00)
committed_bars = {}
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_BUFFER_ADD_COMMITTED'):
        data = e.get('data', {})
        if isinstance(data, dict):
            bar_chicago = data.get('bar_timestamp_chicago', '')
            bar_utc = data.get('bar_timestamp_utc', '')
            if bar_chicago:
                try:
                    bar_time = datetime.fromisoformat(bar_chicago.replace('Z', '+00:00'))
                    if bar_time.hour >= 8 and (bar_time.hour < 11 or (bar_time.hour == 11 and bar_time.minute == 0)):
                        # Extract minute component as key
                        minute_key = bar_time.hour * 60 + bar_time.minute
                        committed_bars[minute_key] = {
                            'bar_chicago': bar_chicago,
                            'bar_utc': bar_utc,
                            'time': bar_time
                        }
                except:
                    pass

print("="*80)
print("MISSING BAR ANALYSIS:")
print("="*80)

# Expected bars: 08:00, 08:01, ..., 10:59 (180 bars)
expected_minutes = []
for hour in range(8, 11):
    for minute in range(60):
        expected_minutes.append(hour * 60 + minute)

print(f"\n  Expected bars: {len(expected_minutes)} (08:00 to 10:59)")
print(f"  Committed bars: {len(committed_bars)}")

# Find missing bars
missing_minutes = []
for minute_key in expected_minutes:
    if minute_key not in committed_bars:
        hour = minute_key // 60
        minute = minute_key % 60
        missing_minutes.append(f"{hour:02d}:{minute:02d}")

if missing_minutes:
    print(f"\n  Missing bars ({len(missing_minutes)}):")
    for missing in missing_minutes[:10]:  # Show first 10
        print(f"    {missing}")
    if len(missing_minutes) > 10:
        print(f"    ... and {len(missing_minutes) - 10} more")
else:
    print(f"\n  All bars present!")

# Check first and last bars
if committed_bars:
    sorted_keys = sorted(committed_bars.keys())
    first_key = sorted_keys[0]
    last_key = sorted_keys[-1]
    
    first_hour = first_key // 60
    first_minute = first_key % 60
    last_hour = last_key // 60
    last_minute = last_key % 60
    
    print(f"\n  Bar time range:")
    print(f"    First bar: {first_hour:02d}:{first_minute:02d}")
    print(f"    Last bar: {last_hour:02d}:{last_minute:02d}")
    
    if first_key != 8 * 60:  # 08:00
        print(f"    WARNING: Missing first bar (08:00)")
    if last_key != 10 * 60 + 59:  # 10:59
        print(f"    WARNING: Missing last bar (10:59)")

# Check if the issue is related to "on close" conversion
print(f"\n{'='*80}")
print("NINJATRADER 'ON CLOSE' ANALYSIS:")
print(f"{'='*80}")
print("  NinjaTrader bars are timestamped at CLOSE time")
print("  We convert to OPEN time by subtracting 1 minute")
print("  So:")
print("    - Bar that closes at 08:00 -> becomes bar that opens at 07:59")
print("    - Bar that closes at 08:01 -> becomes bar that opens at 08:00")
print("    - Bar that closes at 11:00 -> becomes bar that opens at 10:59")
print("\n  Window is [08:00, 11:00) - exclusive on 11:00")
print("  So we need bars with open times: 08:00, 08:01, ..., 10:59")
print("  Which means we need NinjaTrader bars with close times: 08:01, 08:02, ..., 11:00")
print("\n  If BarsRequest requests [08:00, 11:00), it might:")
print("    - Include the 08:00 close-time bar (which becomes 07:59 open-time) → FILTERED OUT")
print("    - Exclude the 11:00 close-time bar (which becomes 10:59 open-time) → MISSING")

print(f"\n{'='*80}")
