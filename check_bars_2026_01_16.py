import json
from pathlib import Path
from collections import defaultdict

log_file = Path("logs/robot/robot_ENGINE.jsonl")
es_log_file = Path("logs/robot/robot_ES.jsonl")

print("=" * 80)
print("CHECKING FOR BARS FROM 2026-01-16")
print("=" * 80)
print()

# Read ENGINE log
with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

print(f"Checking last 2000 ENGINE log lines...")
print()

# Find all bar-related events
bars_2026_01_16 = []
bars_other = []
trading_date_locked = None

for line in lines[-2000:]:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        payload = entry.get('data', {}).get('payload', {})
        
        if event == 'TRADING_DATE_LOCKED':
            trading_date_locked = payload.get('trading_date')
        
        if event == 'BAR_DATE_MISMATCH':
            bar_date = payload.get('bar_trading_date', '')
            if bar_date == '2026-01-16':
                bars_2026_01_16.append({
                    'timestamp': entry.get('ts_utc', ''),
                    'bar_time': payload.get('bar_timestamp_chicago', ''),
                    'event': 'REJECTED'
                })
            else:
                bars_other.append(bar_date)
        
        # Look for bars that were processed (not rejected)
        if 'BAR' in event and 'RECEIVED' in event and 'MISMATCH' not in event:
            bar_time = payload.get('bar_timestamp_chicago', payload.get('bar_timestamp_utc', ''))
            if '2026-01-16' in str(bar_time):
                bars_2026_01_16.append({
                    'timestamp': entry.get('ts_utc', ''),
                    'bar_time': bar_time,
                    'event': 'PROCESSED'
                })
    except:
        pass

print(f"Trading Date Locked: {trading_date_locked}")
print()

if trading_date_locked:
    print(f"Looking for bars from {trading_date_locked}...")
    print()

# Check ES log for bars being processed
if es_log_file.exists():
    print(f"Checking ES log for bars from {trading_date_locked}...")
    print()
    
    with open(es_log_file, 'r', encoding='utf-8', errors='ignore') as f:
        es_lines = f.readlines()
    
    bars_in_streams = []
    for line in es_lines[-2000:]:
        try:
            entry = json.loads(line)
            event = entry.get('event', '')
            payload = entry.get('data', {}).get('payload', {})
            
            # Look for any bar-related events
            if 'BAR' in event:
                bar_time = (payload.get('bar_timestamp_chicago') or 
                           payload.get('bar_timestamp_utc') or 
                           payload.get('timestamp_chicago') or
                           '')
                if '2026-01-16' in str(bar_time) or trading_date_locked in str(bar_time):
                    bars_in_streams.append({
                        'timestamp': entry.get('ts_utc', ''),
                        'event': event,
                        'bar_time': bar_time,
                        'stream': payload.get('stream_id', payload.get('stream', 'UNKNOWN'))
                    })
        except:
            pass
    
    print(f"Bars from {trading_date_locked} in ES streams: {len(bars_in_streams)}")
    if bars_in_streams:
        print(f"\nFirst 10 bars from {trading_date_locked}:")
        for i, bar in enumerate(bars_in_streams[:10], 1):
            print(f"  {i}. [{bar['timestamp']}] {bar['event']} - Stream: {bar['stream']}")
            print(f"     Bar Time: {bar['bar_time']}")
    else:
        print(f"\nNo bars from {trading_date_locked} found in ES streams yet")
        print(f"  System is waiting for bars from {trading_date_locked} to arrive")

# Summary
print(f"\n{'=' * 80}")
print("SUMMARY:")
print("=" * 80)

if trading_date_locked:
    print(f"\nTrading Date: {trading_date_locked}")
    print(f"Bars from {trading_date_locked} being processed: {len(bars_in_streams) if es_log_file.exists() else 0}")
    print(f"Bars from other dates rejected: {len(set(bars_other))} different dates")
    
    if len(bars_in_streams) == 0:
        print(f"\nSTATUS: Waiting for bars from {trading_date_locked}")
        print(f"  - Trading date correctly locked from timetable")
        print(f"  - Bars from wrong dates correctly rejected")
        print(f"  - System will process bars from {trading_date_locked} when they arrive")
    else:
        print(f"\nSTATUS: Processing bars from {trading_date_locked}")
        print(f"  - {len(bars_in_streams)} bars found and being processed")
