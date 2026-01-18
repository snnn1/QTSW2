import json
from pathlib import Path

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("CHECKING BARS AROUND 22:50:35")
print("=" * 80)
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Find events around 22:50:35
target_time = "22:50:35"
target_date = "2026-01-16"

print(f"Looking for events around {target_time}...")
print()

relevant_events = []
for line in lines:
    try:
        entry = json.loads(line)
        ts = entry.get('ts_utc', '')
        event = entry.get('event', '')
        
        # Check if timestamp contains 22:50
        if '22:50' in ts or '22:51' in ts:
            payload = entry.get('data', {}).get('payload', {})
            relevant_events.append({
                'timestamp': ts,
                'event': event,
                'payload': payload
            })
    except:
        pass

print(f"Found {len(relevant_events)} events around {target_time}")
print()

if relevant_events:
    print("Events around 22:50:")
    print("-" * 80)
    for i, evt in enumerate(relevant_events[:30], 1):
        print(f"\n{i}. [{evt['timestamp']}] {evt['event']}")
        
        payload = evt['payload']
        if payload:
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
            if 'instrument' in payload:
                print(f"   Instrument: {payload['instrument']}")
else:
    print("No events found around that time")
    print("\nChecking most recent events...")
    
    recent_events = []
    for line in lines[-100:]:
        try:
            entry = json.loads(line)
            event = entry.get('event', '')
            if 'BAR' in event or 'TRADING_DATE' in event:
                recent_events.append({
                    'timestamp': entry.get('ts_utc', ''),
                    'event': event
                })
        except:
            pass
    
    print(f"\nMost recent bar/trading date events:")
    for evt in recent_events[-20:]:
        print(f"  [{evt['timestamp']}] {evt['event']}")

# Specifically look for the bar at 08:25:00 Chicago (14:25 UTC)
print(f"\n{'=' * 80}")
print("LOOKING FOR BAR AT 2026-01-16 08:25:00 CHICAGO (14:25 UTC)")
print("=" * 80)

bars_0825 = []
for line in lines:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        payload = entry.get('data', {}).get('payload', {})
        
        bar_utc = payload.get('bar_timestamp_utc', '') or payload.get('utc_time', '')
        bar_chicago = payload.get('bar_timestamp_chicago', '') or payload.get('chicago_time', '')
        
        if ('14:25' in bar_utc and '2026-01-16' in bar_utc) or ('08:25' in bar_chicago and '2026-01-16' in bar_chicago):
            bars_0825.append({
                'timestamp': entry.get('ts_utc', ''),
                'event': event,
                'bar_utc': bar_utc,
                'bar_chicago': bar_chicago,
                'bar_date': payload.get('bar_trading_date', ''),
                'locked_date': payload.get('locked_trading_date', ''),
                'full_payload': payload
            })
    except:
        pass

if bars_0825:
    print(f"Found {len(bars_0825)} events for bar at 08:25:00")
    print()
    for i, bar in enumerate(bars_0825[:10], 1):
        print(f"{i}. [{bar['timestamp']}] {bar['event']}")
        print(f"   Bar UTC: {bar['bar_utc']}")
        print(f"   Bar Chicago: {bar['bar_chicago']}")
        print(f"   Bar Date: {bar['bar_date']}")
        print(f"   Locked Date: {bar['locked_date']}")
        print()
else:
    print("No events found for bar at 08:25:00")
    print("\nThis suggests the bar might be getting processed silently (not logged)")
