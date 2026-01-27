#!/usr/bin/env python3
"""Check the complete bar buffer flow with new logging"""
import json
from pathlib import Path
from datetime import datetime
from collections import defaultdict

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

# Find all buffer-related events for NQ2
buffer_events = {
    'BAR_BUFFER_ADD_ATTEMPT': [],
    'BAR_BUFFER_REJECTED': [],
    'BAR_BUFFER_ADD_COMMITTED': [],
    'BAR_BUFFER_CLEARED': [],
    'BAR_ADMISSION_PROOF': []
}

for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2'):
        event_name = e.get('event', '')
        if event_name in buffer_events:
            buffer_events[event_name].append(e)

print("="*80)
print("BAR BUFFER FLOW ANALYSIS FOR NQ2:")
print("="*80)

for event_name, event_list in buffer_events.items():
    print(f"\n  {event_name}: {len(event_list)} events")

# Check bars in [08:00, 11:00) window
print(f"\n{'='*80}")
print("BARS IN [08:00, 11:00) WINDOW:")
print(f"{'='*80}")

# Get unique bars that attempted buffer
attempted_bars = {}
for e in buffer_events['BAR_BUFFER_ADD_ATTEMPT']:
    data = e.get('data', {})
    if isinstance(data, dict):
        bar_utc = data.get('bar_timestamp_utc', '')
        bar_chicago = data.get('bar_timestamp_chicago', '')
        if bar_chicago:
            try:
                bar_time = datetime.fromisoformat(bar_chicago.replace('Z', '+00:00'))
                if bar_time.hour >= 8 and (bar_time.hour < 11 or (bar_time.hour == 11 and bar_time.minute == 0)):
                    attempted_bars[bar_utc] = {
                        'bar_chicago': bar_chicago,
                        'source': data.get('bar_source', 'N/A'),
                        'event_ts': e.get('ts_utc', '')
                    }
            except:
                pass

# Get unique bars that were committed
committed_bars = {}
for e in buffer_events['BAR_BUFFER_ADD_COMMITTED']:
    data = e.get('data', {})
    if isinstance(data, dict):
        bar_utc = data.get('bar_timestamp_utc', '')
        bar_chicago = data.get('bar_timestamp_chicago', '')
        if bar_chicago:
            try:
                bar_time = datetime.fromisoformat(bar_chicago.replace('Z', '+00:00'))
                if bar_time.hour >= 8 and (bar_time.hour < 11 or (bar_time.hour == 11 and bar_time.minute == 0)):
                    committed_bars[bar_utc] = {
                        'bar_chicago': bar_chicago,
                        'source': data.get('bar_source', 'N/A'),
                        'buffer_count': data.get('new_buffer_count', 'N/A'),
                        'event_ts': e.get('ts_utc', '')
                    }
            except:
                pass

# Get rejected bars
rejected_bars = defaultdict(list)
for e in buffer_events['BAR_BUFFER_REJECTED']:
    data = e.get('data', {})
    if isinstance(data, dict):
        bar_utc = data.get('bar_timestamp_utc', '')
        bar_chicago = data.get('bar_timestamp_chicago', '')
        reason = data.get('rejection_reason', 'N/A')
        if bar_chicago:
            try:
                bar_time = datetime.fromisoformat(bar_chicago.replace('Z', '+00:00'))
                if bar_time.hour >= 8 and (bar_time.hour < 11 or (bar_time.hour == 11 and bar_time.minute == 0)):
                    rejected_bars[reason].append({
                        'bar_utc': bar_utc,
                        'bar_chicago': bar_chicago,
                        'source': data.get('bar_source', 'N/A')
                    })
            except:
                pass

print(f"\n  Unique bars that attempted buffer: {len(attempted_bars)}")
print(f"  Unique bars committed to buffer: {len(committed_bars)}")
print(f"  Expected: 180 (3 hours * 60 minutes)")

if rejected_bars:
    print(f"\n  Rejected bars by reason:")
    for reason, bars in rejected_bars.items():
        print(f"    {reason}: {len(bars)} bars")

# Check admission proof vs buffer attempts
admission_passed = {}
for e in buffer_events['BAR_ADMISSION_PROOF']:
    data = e.get('data', {})
    if isinstance(data, dict):
        bar_time_str = data.get('bar_time_chicago', '')
        result = data.get('comparison_result', False)
        bar_utc = data.get('bar_time_raw_utc', '')
        if bar_time_str and (result == True or result == 'True'):
            try:
                bar_time = datetime.fromisoformat(bar_time_str.replace('Z', '+00:00'))
                if bar_time.hour >= 8 and (bar_time.hour < 11 or (bar_time.hour == 11 and bar_time.minute == 0)):
                    admission_passed[bar_utc] = True
            except:
                pass

print(f"\n  Bars that passed admission check: {len(admission_passed)}")
print(f"  Bars that attempted buffer: {len(attempted_bars)}")
print(f"  Bars that passed but never attempted: {len(set(admission_passed.keys()) - set(attempted_bars.keys()))}")
print(f"  Bars that attempted but didn't pass: {len(set(attempted_bars.keys()) - set(admission_passed.keys()))}")

# Check if committed count matches expected
if len(committed_bars) > 0:
    print(f"\n  Buffer has {len(committed_bars)} bars (expected ~180)")
    if len(committed_bars) > 180:
        print(f"  WARNING: More bars than expected!")
    elif len(committed_bars) < 100:
        print(f"  WARNING: Fewer bars than expected!")
else:
    print(f"\n  WARNING: No bars committed to buffer!")

# Show latest HYDRATION_SUMMARY
hydration_summary = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'HYDRATION_SUMMARY'):
        hydration_summary.append(e)

if hydration_summary:
    latest = hydration_summary[-1]
    print(f"\n{'='*80}")
    print("LATEST HYDRATION_SUMMARY:")
    print(f"{'='*80}")
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"  loaded_bars: {data.get('loaded_bars', 'N/A')}")
        print(f"  expected_bars: {data.get('expected_bars', 'N/A')}")
        print(f"  completeness_pct: {data.get('completeness_pct', 'N/A')}")
        print(f"  range_high: {data.get('range_high', 'N/A')}")
        print(f"  range_low: {data.get('range_low', 'N/A')}")

print(f"\n{'='*80}")
