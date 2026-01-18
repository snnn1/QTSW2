import json
from pathlib import Path
from datetime import datetime, timedelta

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("TRADING DATE LOCKING FROM TIMETABLE - LOG ANALYSIS")
print("=" * 80)
print()

# Get current time
now = datetime.now()
recent_threshold = now - timedelta(hours=1)  # Last hour

# Read ENGINE log
with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    engine_lines = f.readlines()

print(f"Analyzing last {len(engine_lines)} log entries...")
print()

# Find key events
key_events = {
    'ENGINE_START': [],
    'TRADING_DATE_LOCKED': [],
    'TIMETABLE_LOADED': [],
    'TIMETABLE_UPDATED': [],
    'TIMETABLE_VALIDATED': [],
    'TIMETABLE_MISSING_TRADING_DATE': [],
    'TIMETABLE_INVALID_TRADING_DATE': [],
    'TIMETABLE_TRADING_DATE_MISMATCH': [],
    'STREAMS_CREATED': [],
    'OPERATOR_BANNER': [],
    'BAR_DATE_MISMATCH': [],
    'BAR_RECEIVED_BEFORE_DATE_LOCKED': []
}

for line in engine_lines[-500:]:  # Last 500 lines
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        ts_str = entry.get('ts_utc', '')
        payload = entry.get('data', {}).get('payload', {})
        
        if event in key_events:
            key_events[event].append({
                'timestamp': ts_str,
                'payload': payload
            })
    except:
        pass

# Print findings
print("KEY EVENTS:")
print("=" * 80)

for event_type in ['ENGINE_START', 'TRADING_DATE_LOCKED', 'TIMETABLE_LOADED', 
                   'TIMETABLE_UPDATED', 'TIMETABLE_VALIDATED', 'STREAMS_CREATED',
                   'OPERATOR_BANNER']:
    events = key_events[event_type]
    if events:
        latest = events[-1]
        print(f"\n{event_type}:")
        print(f"  Timestamp: {latest['timestamp']}")
        payload = latest['payload']
        if payload:
            for key, value in list(payload.items())[:15]:
                if isinstance(value, (dict, list)):
                    value = str(value)[:150]
                print(f"    {key}: {value}")
    else:
        print(f"\n{event_type}: NOT FOUND")

# Check for errors
print(f"\n{'=' * 80}")
print("ERRORS / WARNINGS:")
print("=" * 80)

for event_type in ['TIMETABLE_MISSING_TRADING_DATE', 'TIMETABLE_INVALID_TRADING_DATE', 
                   'TIMETABLE_TRADING_DATE_MISMATCH', 'BAR_RECEIVED_BEFORE_DATE_LOCKED']:
    events = key_events[event_type]
    if events:
        print(f"\n{event_type}: {len(events)} occurrence(s)")
        for event in events[-3:]:  # Show last 3
            print(f"  [{event['timestamp']}]")
            payload = event['payload']
            for key, value in list(payload.items())[:10]:
                if isinstance(value, (dict, list)):
                    value = str(value)[:100]
                print(f"    {key}: {value}")
    else:
        print(f"\n{event_type}: None")

# Check BAR_DATE_MISMATCH
mismatches = key_events['BAR_DATE_MISMATCH']
if mismatches:
    print(f"\n{'=' * 80}")
    print(f"BAR_DATE_MISMATCH Events: {len(mismatches)}")
    print("=" * 80)
    
    # Group by locked date
    by_locked_date = {}
    for m in mismatches:
        locked = m['payload'].get('locked_trading_date', 'UNKNOWN')
        if locked not in by_locked_date:
            by_locked_date[locked] = []
        by_locked_date[locked].append(m)
    
    for locked_date, events in by_locked_date.items():
        print(f"\n  Locked Date: {locked_date} ({len(events)} mismatches)")
        if events:
            first = events[0]
            print(f"    First mismatch: {first['timestamp']}")
            print(f"    Bar date: {first['payload'].get('bar_trading_date', 'N/A')}")
            print(f"    Bar time: {first['payload'].get('bar_timestamp_chicago', 'N/A')}")
else:
    print(f"\n{'=' * 80}")
    print("✅ No BAR_DATE_MISMATCH events - Trading date matches all bars!")
    print("=" * 80)

# Check TRADING_DATE_LOCKED source
locked_events = key_events['TRADING_DATE_LOCKED']
if locked_events:
    print(f"\n{'=' * 80}")
    print("TRADING_DATE_LOCKED Analysis:")
    print("=" * 80)
    for event in locked_events:
        payload = event['payload']
        source = payload.get('source', 'UNKNOWN')
        trading_date = payload.get('trading_date', 'N/A')
        print(f"\n  Timestamp: {event['timestamp']}")
        print(f"  Source: {source}")
        print(f"  Trading Date: {trading_date}")
        if source == 'TIMETABLE':
            print(f"  ✅ Correctly locked from timetable!")
        else:
            print(f"  ⚠️  Locked from {source} (expected TIMETABLE)")
