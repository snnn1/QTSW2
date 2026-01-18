import json
from pathlib import Path
from datetime import datetime

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("MOST RECENT LOG EVENTS")
print("=" * 80)
print()

# Read last 500 lines
with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    all_lines = f.readlines()

print(f"Total log lines: {len(all_lines)}")
print(f"Checking last 500 lines...")
print()

# Find relevant events
relevant_events = []
for line in all_lines[-500:]:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        if event in ['ENGINE_START', 'TRADING_DATE_LOCKED', 'TIMETABLE_LOADED', 
                     'TIMETABLE_UPDATED', 'TIMETABLE_VALIDATED', 'STREAMS_CREATED',
                     'OPERATOR_BANNER', 'BAR_DATE_MISMATCH', 'BAR_RECEIVED_BEFORE_DATE_LOCKED',
                     'TIMETABLE_MISSING_TRADING_DATE', 'TIMETABLE_INVALID_TRADING_DATE']:
            relevant_events.append(entry)
    except:
        pass

print(f"Found {len(relevant_events)} relevant events")
print()

# Show last 20 relevant events
for i, event in enumerate(relevant_events[-20:], 1):
    print(f"{i}. [{event.get('ts_utc', 'N/A')}] {event.get('event', 'N/A')}")
    payload = event.get('data', {}).get('payload', {})
    if payload:
        # Show key fields
        if 'trading_date' in payload:
            print(f"   Trading Date: {payload.get('trading_date', 'N/A')}")
        if 'source' in payload:
            print(f"   Source: {payload.get('source', 'N/A')}")
        if 'locked_trading_date' in payload:
            print(f"   Locked: {payload.get('locked_trading_date', 'N/A')}, Bar: {payload.get('bar_trading_date', 'N/A')}")
        if 'stream_count' in payload:
            print(f"   Stream Count: {payload.get('stream_count', 'N/A')}")
        if 'timetable_path' in payload:
            print(f"   Timetable Path: {payload.get('timetable_path', 'N/A')}")
    print()
