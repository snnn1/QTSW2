import json
from pathlib import Path
from datetime import datetime

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("CHECKING BAR FROM 2026-01-16 08:25:00")
print("=" * 80)
print()

target_time = "2026-01-16T14:25:00"  # UTC time from the log
target_date = "2026-01-16"

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Find events around that time
print(f"Looking for events around {target_time} UTC (2026-01-16 08:25:00 Chicago)...")
print()

relevant_events = []
for line in lines:
    try:
        entry = json.loads(line)
        ts = entry.get('ts_utc', '')
        event = entry.get('event', '')
        
        # Check if timestamp is around target time (within 5 minutes)
        if target_time in ts or '14:25' in ts or '14:26' in ts or '14:24' in ts:
            relevant_events.append({
                'timestamp': ts,
                'event': event,
                'entry': entry
            })
        
        # Also check for bars from 2026-01-16
        if event == 'BAR_DATE_MISMATCH':
            payload = entry.get('data', {}).get('payload', {})
            bar_date = payload.get('bar_trading_date', '')
            bar_utc = payload.get('bar_timestamp_utc', '')
            if target_date in bar_date or target_time[:13] in bar_utc:
                relevant_events.append({
                    'timestamp': ts,
                    'event': event,
                    'entry': entry
                })
        
        if event == 'ENGINE_BAR_HEARTBEAT':
            payload = entry.get('data', {}).get('payload', {})
            bar_utc = payload.get('utc_time', '')
            if target_time[:13] in bar_utc:
                relevant_events.append({
                    'timestamp': ts,
                    'event': event,
                    'entry': entry
                })
    except:
        pass

print(f"Found {len(relevant_events)} relevant events")
print()

if relevant_events:
    print("Relevant events:")
    print("-" * 80)
    for i, evt in enumerate(relevant_events[:20], 1):
        print(f"\n{i}. [{evt['timestamp']}] {evt['event']}")
        
        payload = evt['entry'].get('data', {}).get('payload', {})
        if payload:
            print(f"   Payload keys: {list(payload.keys())}")
            if 'bar_timestamp_utc' in payload:
                print(f"   Bar UTC: {payload['bar_timestamp_utc']}")
            if 'bar_timestamp_chicago' in payload:
                print(f"   Bar Chicago: {payload['bar_timestamp_chicago']}")
            if 'bar_trading_date' in payload:
                print(f"   Bar Date: {payload['bar_trading_date']}")
            if 'locked_trading_date' in payload:
                print(f"   Locked Date: {payload['locked_trading_date']}")
            if 'utc_time' in payload:
                print(f"   UTC Time: {payload['utc_time']}")
            if 'chicago_time' in payload:
                print(f"   Chicago Time: {payload['chicago_time']}")
else:
    print("No events found around that time")
    print("\nChecking for ANY bars from 2026-01-16...")
    
    bars_from_16 = []
    for line in lines[-2000:]:
        try:
            entry = json.loads(line)
            event = entry.get('event', '')
            if event == 'BAR_DATE_MISMATCH':
                payload = entry.get('data', {}).get('payload', {})
                bar_date = payload.get('bar_trading_date', '')
                if bar_date == '2026-01-16':
                    bars_from_16.append({
                        'timestamp': entry.get('ts_utc', ''),
                        'bar_utc': payload.get('bar_timestamp_utc', ''),
                        'bar_chicago': payload.get('bar_timestamp_chicago', ''),
                        'bar_date': bar_date
                    })
        except:
            pass
    
    if bars_from_16:
        print(f"\nFound {len(bars_from_16)} bars from 2026-01-16!")
        print("First 5:")
        for i, bar in enumerate(bars_from_16[:5], 1):
            print(f"  {i}. [{bar['timestamp']}] UTC: {bar['bar_utc']} | Chicago: {bar['bar_chicago']}")
    else:
        print("No bars from 2026-01-16 found in BAR_DATE_MISMATCH events")
        print("\nThis suggests bars from 2026-01-16 might be getting processed (not mismatched)")

# Check for processed bars (not mismatched)
print(f"\n{'=' * 80}")
print("CHECKING FOR PROCESSED BARS (not in BAR_DATE_MISMATCH)")
print("=" * 80)

processed_bars = []
for line in lines[-2000:]:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        ts = entry.get('ts_utc', '')
        
        # Look for bar processing events that aren't mismatches
        if event in ['ENGINE_BAR_HEARTBEAT'] and '14:25' in ts:
            payload = entry.get('data', {}).get('payload', {})
            bar_utc = payload.get('utc_time', '') or payload.get('bar_timestamp_utc', '')
            bar_chicago = payload.get('chicago_time', '') or payload.get('bar_timestamp_chicago', '')
            if '2026-01-16' in bar_utc or '2026-01-16' in bar_chicago:
                processed_bars.append({
                    'timestamp': ts,
                    'event': event,
                    'bar_utc': bar_utc,
                    'bar_chicago': bar_chicago
                })
    except:
        pass

if processed_bars:
    print(f"Found {len(processed_bars)} processed bar events from 2026-01-16 around 14:25 UTC")
    for bar in processed_bars[:10]:
        print(f"  [{bar['timestamp']}] {bar['event']} | UTC: {bar['bar_utc']} | Chicago: {bar['bar_chicago']}")
else:
    print("No processed bar events found around that time")
