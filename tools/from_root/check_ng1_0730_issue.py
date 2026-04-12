#!/usr/bin/env python3
"""
Check why NG1 triggered at 07:30 instead of 09:00
"""

import json
import sys
from pathlib import Path
from datetime import datetime, timedelta
import pytz

def parse_timestamp(ts_str):
    """Parse timestamp string to datetime."""
    if not ts_str:
        return None
    try:
        if 'T' in ts_str or '+' in ts_str or 'Z' in ts_str:
            return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
        for fmt in ['%Y-%m-%d %H:%M:%S.%f', '%Y-%m-%d %H:%M:%S']:
            try:
                return datetime.strptime(ts_str[:19], fmt).replace(tzinfo=pytz.UTC)
            except:
                continue
    except:
        pass
    return None

def main():
    print("="*80)
    print("NG1 07:30 vs 09:00 INVESTIGATION")
    print("="*80)
    
    # Check today's date
    today = datetime.now(pytz.timezone('America/Chicago')).date()
    print(f"\nToday's date: {today}")
    
    # Check timetable
    timetable_path = Path("data/timetable/timetable_current.json")
    if timetable_path.exists():
        timetable = json.load(open(timetable_path, 'r', encoding='utf-8'))
        print(f"\nTimetable file: {timetable_path}")
        print(f"Timetable trading_date: {timetable.get('trading_date')}")
        print(f"Timetable as_of: {timetable.get('as_of')}")
        
        streams = timetable.get('streams', [])
        ng1_config = None
        for s in streams:
            if isinstance(s, dict) and s.get('stream') == 'NG1':
                ng1_config = s
                break
        
        if ng1_config:
            print(f"\nNG1 Configuration:")
            print(f"  Enabled: {ng1_config.get('enabled')}")
            print(f"  Slot time: {ng1_config.get('slot_time')}")
            print(f"  Session: {ng1_config.get('session')}")
            print(f"  Instrument: {ng1_config.get('instrument')}")
        else:
            print("\n[ERROR] NG1 not found in timetable!")
    
    # Check logs for NG1 events today
    log_dir = Path("logs/robot")
    if not log_dir.exists():
        print("\n[ERROR] Log directory not found!")
        return 1
    
    print("\n" + "="*80)
    print("NG1 EVENTS TODAY")
    print("="*80)
    
    today_str = today.strftime("%Y-%m-%d")
    ng1_events = []
    
    # Check all robot log files
    for log_file in log_dir.glob("robot_*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        event = json.loads(line)
                        # Check if it's NG1 related
                        stream = event.get('stream', event.get('stream_id', ''))
                        trading_date = event.get('trading_date', '')
                        event_type = event.get('event_type', event.get('EventType', ''))
                        
                        if stream == 'NG1' and trading_date == today_str:
                            ts = parse_timestamp(event.get('ts_utc', event.get('timestamp', '')))
                            if ts:
                                ng1_events.append((ts, event_type, event))
                    except:
                        pass
        except:
            pass
    
    ng1_events.sort(key=lambda x: x[0])
    
    print(f"\nFound {len(ng1_events)} NG1 events today")
    
    # Look for key events
    print("\nKey Events:")
    chicago_tz = pytz.timezone('America/Chicago')
    
    for ts, event_type, event in ng1_events[:50]:  # First 50 events
        ts_chicago = ts.astimezone(chicago_tz)
        slot_time = event.get('slot_time_chicago', event.get('SlotTimeChicago', ''))
        data = event.get('data', {})
        if isinstance(data, dict):
            slot_from_data = data.get('slot_time_chicago', '')
        else:
            slot_from_data = ''
        
        slot_display = slot_time or slot_from_data or 'N/A'
        
        # Highlight important events
        if 'RANGE_LOCKED' in event_type or 'STOP_BRACKETS' in event_type or 'STREAM_CREATED' in event_type or 'SLOT_TIME' in event_type:
            print(f"  *** {ts_chicago.strftime('%H:%M:%S')} ({ts.strftime('%H:%M:%S')} UTC) | {event_type} | Slot: {slot_display}")
        elif ts_chicago.hour == 7 and ts_chicago.minute >= 25 and ts_chicago.minute <= 35:
            print(f"  >>> {ts_chicago.strftime('%H:%M:%S')} ({ts.strftime('%H:%M:%S')} UTC) | {event_type} | Slot: {slot_display}")
    
    # Check for stream creation events
    print("\n" + "="*80)
    print("STREAM CREATION EVENTS")
    print("="*80)
    
    for ts, event_type, event in ng1_events:
        if 'STREAM_CREATED' in event_type or 'STREAM_INITIALIZED' in event_type:
            ts_chicago = ts.astimezone(chicago_tz)
            slot_time = event.get('slot_time_chicago', event.get('SlotTimeChicago', ''))
            data = event.get('data', {})
            if isinstance(data, dict):
                slot_from_data = data.get('slot_time_chicago', '')
            else:
                slot_from_data = ''
            
            slot_display = slot_time or slot_from_data or 'N/A'
            print(f"  {ts_chicago.strftime('%Y-%m-%d %H:%M:%S')} ({ts.strftime('%H:%M:%S')} UTC) | Slot: {slot_display}")
            print(f"    Event: {event_type}")
            print(f"    Full event: {json.dumps(event, indent=2)[:500]}")
            print()
    
    # Check ENGINE logs for timetable loading
    print("\n" + "="*80)
    print("TIMETABLE LOADING EVENTS")
    print("="*80)
    
    engine_log = log_dir / "robot_ENGINE.jsonl"
    if engine_log.exists():
        try:
            with open(engine_log, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        event = json.loads(line)
                        event_type = event.get('event_type', event.get('EventType', ''))
                        trading_date = event.get('trading_date', '')
                        
                        if ('TIMETABLE' in event_type.upper() or 'STREAM_CREATED' in event_type) and trading_date == today_str:
                            ts = parse_timestamp(event.get('ts_utc', event.get('timestamp', '')))
                            if ts:
                                ts_chicago = ts.astimezone(chicago_tz)
                                data = event.get('data', {})
                                print(f"  {ts_chicago.strftime('%H:%M:%S')} ({ts.strftime('%H:%M:%S')} UTC) | {event_type}")
                                if isinstance(data, dict) and 'stream' in data:
                                    print(f"    Stream: {data.get('stream')} | Slot: {data.get('slot_time', 'N/A')}")
                    except:
                        pass
        except:
            pass
    
    return 0

if __name__ == '__main__':
    sys.exit(main())
