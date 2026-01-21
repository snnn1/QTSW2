"""
Check Robot Status - Verify CME rollover and session window are working
"""
import json
from pathlib import Path
from datetime import datetime
import pytz

engine_log = Path("logs/robot/robot_ENGINE.jsonl")
if not engine_log.exists():
    print(f"Engine log not found: {engine_log}")
    exit(1)

events = []
with open(engine_log, 'r', encoding='utf-8') as f:
    for line in f:
        line = line.strip()
        if line:
            try:
                events.append(json.loads(line))
            except:
                pass

print("="*80)
print("ROBOT STATUS CHECK")
print("="*80)

# Check trading date locked
trading_date_events = [e for e in events if e.get('event') == 'TRADING_DATE_LOCKED']
if trading_date_events:
    latest = trading_date_events[-1]
    payload = latest.get('data', {}).get('payload', {})
    print(f"\n[TRADING_DATE_LOCKED]")
    print(f"  Trading Date: {payload.get('trading_date', 'N/A')}")
    print(f"  Source: {payload.get('source', 'N/A')}")
    print(f"  Timestamp: {latest.get('ts_utc', 'N/A')}")
else:
    print("\n[WARNING] No TRADING_DATE_LOCKED events found")

# Check session start times
session_start_events = [e for e in events if e.get('event') == 'SESSION_START_TIME_SET']
if session_start_events:
    print(f"\n[SESSION_START_TIME_SET] ({len(session_start_events)} events)")
    for e in session_start_events[-5:]:
        payload = e.get('data', {}).get('payload', {})
        print(f"  {payload.get('instrument', 'N/A')}: {payload.get('session_start_time', 'N/A')}")
else:
    print("\n[WARNING] No SESSION_START_TIME_SET events found")

# Check bar acceptance/rejection
bar_delivery = [e for e in events if e.get('event') == 'BAR_DELIVERY_TO_STREAM']
bar_mismatch = [e for e in events if e.get('event') == 'BAR_DATE_MISMATCH']

print(f"\n[BAR STATISTICS]")
print(f"  Bars Delivered: {len(bar_delivery)}")
print(f"  Bars Rejected (DATE_MISMATCH): {len(bar_mismatch)}")

if bar_delivery:
    print(f"\n[RECENT BAR DELIVERIES]")
    for e in bar_delivery[-5:]:
        payload = e.get('data', {}).get('payload', {})
        print(f"  {payload.get('instrument', 'N/A')} {payload.get('stream', 'N/A')}: {payload.get('bar_timestamp_chicago', 'N/A')}")

if bar_mismatch:
    print(f"\n[RECENT BAR REJECTIONS]")
    for e in bar_mismatch[-5:]:
        payload = e.get('data', {}).get('payload', {})
        print(f"  {payload.get('instrument', 'N/A')}: {payload.get('bar_timestamp_chicago', 'N/A')} - {payload.get('rejection_reason', 'N/A')}")
        print(f"    Session Window: {payload.get('session_start_chicago', 'N/A')} to {payload.get('session_end_chicago', 'N/A')}")

# Check current trading date from logs
if events:
    latest_event = events[-1]
    trading_date = latest_event.get('trading_date', 'N/A')
    print(f"\n[CURRENT STATE]")
    print(f"  Latest Event Trading Date: {trading_date}")
    print(f"  Latest Event: {latest_event.get('event', 'N/A')}")
    print(f"  Latest Timestamp: {latest_event.get('ts_utc', 'N/A')}")

# Verify timetable
timetable_path = Path("data/timetable/timetable_current.json")
if timetable_path.exists():
    timetable = json.loads(timetable_path.read_text())
    print(f"\n[TIMETABLE FILE]")
    print(f"  Trading Date: {timetable.get('trading_date', 'N/A')}")
    print(f"  As Of: {timetable.get('as_of', 'N/A')}")
    
    # Check if trading_date matches CME rollover
    chicago_tz = pytz.timezone("America/Chicago")
    as_of_str = timetable.get('as_of', '')
    if as_of_str:
        as_of_dt = datetime.fromisoformat(as_of_str)
        chicago_time = as_of_dt.astimezone(chicago_tz)
        chicago_hour = chicago_time.hour
        from datetime import timedelta
        expected_trading_date = (chicago_time.date() + timedelta(days=1)).isoformat() if chicago_hour >= 17 else chicago_time.date().isoformat()
        actual_trading_date = timetable.get('trading_date', '')
        match = actual_trading_date == expected_trading_date
        print(f"  Expected (CME rollover): {expected_trading_date}")
        print(f"  Actual: {actual_trading_date}")
        print(f"  Match: {match} {'[OK]' if match else '[FAIL]'}")

print("\n" + "="*80)
