import json
from pathlib import Path

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("TRADING DATE STATUS CHECK")
print("=" * 80)
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Find all TRADING_DATE_LOCKED events
trading_date_events = []
for line in lines:
    try:
        entry = json.loads(line)
        if entry.get('event') == 'TRADING_DATE_LOCKED':
            payload = entry.get('data', {}).get('payload', {})
            trading_date_events.append({
                'timestamp': entry.get('ts_utc', ''),
                'trading_date': payload.get('trading_date', ''),
                'source': payload.get('source', ''),
                'timetable_path': payload.get('timetable_path', '')
            })
    except:
        pass

print(f"Found {len(trading_date_events)} TRADING_DATE_LOCKED events")
print()

if trading_date_events:
    print("All TRADING_DATE_LOCKED events:")
    print("-" * 80)
    for i, event in enumerate(trading_date_events, 1):
        print(f"{i}. [{event['timestamp']}]")
        print(f"   Trading Date: {event['trading_date']}")
        print(f"   Source: {event['source']}")
        print(f"   Timetable: {event['timetable_path']}")
        print()
    
    latest = trading_date_events[-1]
    print("=" * 80)
    print(f"LATEST TRADING DATE: {latest['trading_date']}")
    print(f"Locked at: {latest['timestamp']}")
    print(f"Source: {latest['source']}")
    print("=" * 80)
else:
    print("NO TRADING_DATE_LOCKED events found in entire log!")
    print("This means trading date was never locked from timetable.")
    print()
    print("Checking for ENGINE_START events...")
    
    start_events = []
    for line in lines[-500:]:
        try:
            entry = json.loads(line)
            if entry.get('event') == 'ENGINE_START':
                start_events.append(entry.get('ts_utc', ''))
        except:
            pass
    
    if start_events:
        print(f"Found {len(start_events)} ENGINE_START events")
        print(f"Most recent: {start_events[-1]}")
    else:
        print("No ENGINE_START events found either!")

# Check recent bar events
print(f"\n{'=' * 80}")
print("RECENT BAR EVENTS (last 20):")
print("=" * 80)

bar_count = 0
for line in reversed(lines):
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        if 'BAR' in event:
            bar_count += 1
            if bar_count <= 20:
                payload = entry.get('data', {}).get('payload', {})
                bar_date = payload.get('bar_trading_date', '') or payload.get('bar_trading_date', '')
                print(f"[{entry.get('ts_utc', '')[:19]}] {event:30} | Date: {bar_date}")
    except:
        pass
