#!/usr/bin/env python3
"""Check range calculation details for NQ2"""
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

# Find latest HYDRATION_SUMMARY for NQ2
today_summary = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'HYDRATION_SUMMARY' and 
        e.get('stream') == 'NQ2'):
        today_summary.append(e)

if today_summary:
    latest = today_summary[-1]
    print("="*80)
    print("LATEST HYDRATION_SUMMARY RANGE:")
    print("="*80)
    print(f"  Timestamp: {latest.get('ts_utc', 'N/A')[:19]}")
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"  Range high: {data.get('reconstructed_range_high', 'N/A')}")
        print(f"  Range low: {data.get('reconstructed_range_low', 'N/A')}")
        print(f"  Range start: {data.get('range_start_chicago', 'N/A')}")
        print(f"  Slot time: {data.get('slot_time_chicago', 'N/A')}")
        print(f"  Now Chicago: {data.get('now_chicago', 'N/A')}")
        print(f"  Loaded bars: {data.get('loaded_bars', 'N/A')}")
        print(f"  Historical bars: {data.get('historical_bar_count', 'N/A')}")

# Check for RANGE_BUILD_START or RANGE_COMPUTED events
range_events = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        ('RANGE' in e.get('event', '') or 'COMPUTE_RANGE' in e.get('event', ''))):
        range_events.append(e)

if range_events:
    print(f"\n{'='*80}")
    print("RANGE COMPUTATION EVENTS:")
    print(f"{'='*80}")
    for e in range_events[-5:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        event_name = e.get('event', 'N/A')
        print(f"  {ts} | {event_name}")
        data = e.get('data', {})
        if isinstance(data, dict):
            if 'range_high' in data or 'high' in data:
                print(f"    Range high: {data.get('range_high', data.get('high', 'N/A'))}")
                print(f"    Range low: {data.get('range_low', data.get('low', 'N/A'))}")

# Check for bar admission events to see actual bar prices
bar_events = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        'BAR_ADMISSION' in e.get('event', '')):
        bar_events.append(e)

if bar_events:
    print(f"\n{'='*80}")
    print("RECENT BAR PRICES (last 5):")
    print(f"{'='*80}")
    for e in bar_events[-5:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        data = e.get('data', {})
        if isinstance(data, dict):
            payload = data.get('payload', {})
            if isinstance(payload, dict):
                print(f"  {ts} | High: {payload.get('high', 'N/A')} | Low: {payload.get('low', 'N/A')} | Close: {payload.get('close', 'N/A')}")

# Check BARSREQUEST to see what instrument bars were requested
barsrequest = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        'BARSREQUEST_EXECUTED' in e.get('event', '')):
        barsrequest.append(e)

if barsrequest:
    latest_br = barsrequest[-1]
    print(f"\n{'='*80}")
    print("BARSREQUEST DETAILS:")
    print(f"{'='*80}")
    data = latest_br.get('data', {})
    if isinstance(data, dict):
        print(f"  Instrument: {data.get('instrument', 'N/A')}")
        print(f"  Bars returned: {data.get('bars_returned', data.get('bar_count', 'N/A'))}")
        print(f"  Start time: {data.get('start_time', 'N/A')}")
        print(f"  End time: {data.get('end_time', 'N/A')}")

print(f"\n{'='*80}")
print("COMPARISON:")
print(f"{'='*80}")
print(f"  System calculated range high: 25742.25")
print(f"  Your MNQ range high: 25903")
print(f"  Difference: {25903 - 25742.25:.2f} points")
print(f"\n  Possible reasons:")
print(f"  1. Range was calculated earlier (timestamp check needed)")
print(f"  2. Price conversion issue (MNQ vs NQ)")
print(f"  3. Different bars used in calculation")
print(f"  4. Range window timing difference")
print(f"{'='*80}")
