#!/usr/bin/env python3
"""Check NQ2/MNQ BarsRequest status"""
import json
from pathlib import Path

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

# Filter for today's BARSREQUEST events for NQ/MNQ
today_events = [e for e in events if e.get('ts_utc', '').startswith('2026-01-26')]

barsrequest_events = []
for e in today_events:
    event_name = e.get('event', '')
    data = e.get('data', {})
    instrument = e.get('instrument', '')
    
    # Check if it's a BARSREQUEST event for NQ/MNQ
    if ('BARSREQUEST' in event_name or 'PRE_HYDRATION_BARS' in event_name):
        # Check if it's for NQ or MNQ
        if isinstance(data, dict):
            payload_instrument = data.get('instrument', '')
            if 'NQ' in str(instrument) or 'NQ' in str(payload_instrument) or 'MNQ' in str(instrument) or 'MNQ' in str(payload_instrument):
                barsrequest_events.append(e)

print("="*80)
print("NQ2/MNQ BARSREQUEST STATUS (TODAY)")
print("="*80)
print(f"\nTotal BARSREQUEST events: {len(barsrequest_events)}")

if barsrequest_events:
    print("\nRecent BARSREQUEST events:")
    for e in barsrequest_events[-10:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        event_name = e.get('event', 'N/A')
        data = e.get('data', {})
        print(f"\n  {ts} | {event_name}")
        if isinstance(data, dict):
            print(f"    Instrument: {data.get('instrument', 'N/A')}")
            print(f"    Bar count: {data.get('bar_count', data.get('accepted_bar_count', data.get('raw_bar_count', 'N/A')))}")
            print(f"    Filtered future: {data.get('filtered_future_count', data.get('filtered_future', 'N/A'))}")
            print(f"    Filtered partial: {data.get('filtered_partial_count', data.get('filtered_partial', 'N/A'))}")
            if 'stream_id' in data:
                print(f"    Stream ID: {data.get('stream_id', 'N/A')}")
else:
    print("\n[WARN] No BARSREQUEST events found for NQ/MNQ today!")
    print("  This suggests:")
    print("  - BarsRequest may not have been called yet")
    print("  - BarsRequest failed silently")
    print("  - Events are logged under different instrument name")

# Check for any PRE_HYDRATION_BARS_LOADED events
loaded_events = [e for e in today_events if e.get('event') == 'PRE_HYDRATION_BARS_LOADED']
if loaded_events:
    print(f"\n{'='*80}")
    print("PRE_HYDRATION_BARS_LOADED EVENTS:")
    print(f"{'='*80}")
    for e in loaded_events[-5:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        data = e.get('data', {})
        if isinstance(data, dict):
            print(f"  {ts} | Instrument: {data.get('instrument', 'N/A')} | Bars: {data.get('bar_count', 'N/A')}")

print("\n" + "="*80)
