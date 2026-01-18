import json
from pathlib import Path
from datetime import datetime, timedelta

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("POST-RESTART LOG ANALYSIS (ES Only)")
print("=" * 80)
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    all_lines = f.readlines()

# Get current time and look at last hour
now = datetime.now()
one_hour_ago = now - timedelta(hours=1)

# Find key events from recent restart
key_events = {
    'ENGINE_START': [],
    'TRADING_DATE_LOCKED': [],
    'STREAMS_CREATED': [],
    'OPERATOR_BANNER': [],
    'ADAPTER_SELECTED': [],
    'SIM_ACCOUNT_VERIFIED': [],
    'PLAYBACK_ACCOUNT_DETECTED': [],
    'BAR_DATE_MISMATCH': []
}

for line in all_lines[-500:]:  # Check last 500 lines
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        ts_str = entry.get('ts_utc', '')
        
        if ts_str:
            try:
                ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00').replace('+00:00', ''))
                # Check if within last hour
                if ts >= one_hour_ago:
                    if event in key_events:
                        key_events[event].append({
                            'timestamp': ts_str,
                            'payload': entry.get('data', {}).get('payload', {})
                        })
            except:
                pass
    except:
        pass

# Print findings
for event_type in ['ENGINE_START', 'TRADING_DATE_LOCKED', 'STREAMS_CREATED', 
                   'OPERATOR_BANNER', 'ADAPTER_SELECTED', 'SIM_ACCOUNT_VERIFIED',
                   'PLAYBACK_ACCOUNT_DETECTED']:
    events = key_events[event_type]
    if events:
        latest = events[-1]
        print(f"\n{event_type}:")
        print(f"  Timestamp: {latest['timestamp']}")
        payload = latest['payload']
        if payload:
            for key, value in list(payload.items())[:15]:
                if isinstance(value, (dict, list)):
                    value = str(value)[:100]
                print(f"    {key}: {value}")
    else:
        print(f"\n{event_type}: NOT FOUND (in last hour)")

# Check BAR_DATE_MISMATCH
mismatches = key_events['BAR_DATE_MISMATCH']
if mismatches:
    print(f"\n{'=' * 80}")
    print(f"BAR_DATE_MISMATCH Events (last hour): {len(mismatches)}")
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
    print("No BAR_DATE_MISMATCH events in last hour - GOOD!")
    print("=" * 80)

# Check for ES-specific events
print(f"\n{'=' * 80}")
print("ES-Specific Events:")
print("=" * 80)

es_events = []
for line in all_lines[-200:]:
    try:
        entry = json.loads(line)
        payload = entry.get('data', {}).get('payload', {})
        instrument = payload.get('instrument', '')
        if instrument == 'ES':
            es_events.append({
                'timestamp': entry.get('ts_utc', ''),
                'event': entry.get('event', ''),
                'instrument': instrument
            })
    except:
        pass

if es_events:
    print(f"\nFound {len(es_events)} ES-related events (last 200 lines):")
    for event in es_events[-10:]:
        print(f"  [{event['timestamp']}] {event['event']}")
