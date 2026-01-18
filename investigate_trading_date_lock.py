import json
from pathlib import Path
from datetime import datetime

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("INVESTIGATING TRADING DATE LOCK ISSUE")
print("=" * 80)
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    all_lines = f.readlines()

# Find all TRADING_DATE_LOCKED events
locked_events = []
for line in all_lines:
    try:
        entry = json.loads(line)
        if entry.get('event') == 'TRADING_DATE_LOCKED':
            locked_events.append({
                'timestamp': entry.get('ts_utc', ''),
                'payload': entry.get('data', {}).get('payload', {})
            })
    except:
        pass

print(f"Found {len(locked_events)} TRADING_DATE_LOCKED events:")
for event in locked_events:
    print(f"\n  Timestamp: {event['timestamp']}")
    payload = event['payload']
    for key, value in payload.items():
        print(f"    {key}: {value}")

# Find first BAR_DATE_MISMATCH to see when date was already locked
print("\n" + "=" * 80)
print("First BAR_DATE_MISMATCH Events:")
print("=" * 80)

mismatches = []
for line in all_lines:
    try:
        entry = json.loads(line)
        if entry.get('event') == 'BAR_DATE_MISMATCH':
            payload = entry.get('data', {}).get('payload', {})
            mismatches.append({
                'timestamp': entry.get('ts_utc', ''),
                'locked_date': payload.get('locked_trading_date', ''),
                'bar_date': payload.get('bar_trading_date', ''),
                'bar_time': payload.get('bar_timestamp_chicago', '')
            })
    except:
        pass

if mismatches:
    print(f"\nFound {len(mismatches)} BAR_DATE_MISMATCH events")
    print(f"First mismatch:")
    first = mismatches[0]
    print(f"  Timestamp: {first['timestamp']}")
    print(f"  Locked Date: {first['locked_date']}")
    print(f"  Bar Date: {first['bar_date']}")
    print(f"  Bar Time: {first['bar_time']}")
    
    # Check if there are bars BEFORE the first mismatch that might have locked the date
    print("\n" + "=" * 80)
    print("Checking bars BEFORE first mismatch:")
    print("=" * 80)
    
    first_mismatch_time = first['timestamp']
    bars_before = []
    for line in all_lines:
        try:
            entry = json.loads(line)
            ts = entry.get('ts_utc', '')
            if ts < first_mismatch_time:
                # Check if this is a bar-related event
                event = entry.get('event', '')
                if 'BAR' in event or 'ENGINE_BAR' in event:
                    bars_before.append({
                        'timestamp': ts,
                        'event': event,
                        'payload': entry.get('data', {}).get('payload', {})
                    })
        except:
            pass
    
    print(f"\nFound {len(bars_before)} bar-related events before first mismatch")
    if bars_before:
        print("Last 10 bar events before mismatch:")
        for bar in bars_before[-10:]:
            print(f"  [{bar['timestamp']}] {bar['event']}")
            payload = bar['payload']
            if 'bar_timestamp_chicago' in payload:
                print(f"    Bar Time: {payload.get('bar_timestamp_chicago', '')}")
                print(f"    Bar Date: {payload.get('bar_trading_date', 'N/A')}")

# Check for ENGINE_START
print("\n" + "=" * 80)
print("ENGINE_START Events:")
print("=" * 80)

start_events = []
for line in all_lines:
    try:
        entry = json.loads(line)
        if entry.get('event') == 'ENGINE_START':
            start_events.append({
                'timestamp': entry.get('ts_utc', ''),
                'payload': entry.get('data', {}).get('payload', {})
            })
    except:
        pass

print(f"\nFound {len(start_events)} ENGINE_START events:")
for event in start_events[-5:]:
    print(f"  [{event['timestamp']}] ENGINE_START")
