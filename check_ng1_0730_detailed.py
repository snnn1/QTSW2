#!/usr/bin/env python3
"""
Detailed check of NG1 events around 07:30 today
"""

import json
import sys
from pathlib import Path
from datetime import datetime
import pytz

def parse_timestamp(ts_str):
    """Parse timestamp string to datetime."""
    if not ts_str:
        return None
    try:
        if 'T' in ts_str or '+' in ts_str or 'Z' in ts_str:
            return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        pass
    return None

def main():
    chicago_tz = pytz.timezone('America/Chicago')
    today = datetime.now(chicago_tz).date()
    today_str = today.strftime("%Y-%m-%d")
    
    # Time window: 07:00 to 08:00 Chicago time
    start_chicago = chicago_tz.localize(datetime.combine(today, datetime.min.time().replace(hour=7, minute=0)))
    end_chicago = chicago_tz.localize(datetime.combine(today, datetime.min.time().replace(hour=8, minute=0)))
    start_utc = start_chicago.astimezone(pytz.UTC)
    end_utc = end_chicago.astimezone(pytz.UTC)
    
    print("="*80)
    print(f"NG1 EVENTS BETWEEN 07:00-08:00 CHICAGO TIME ({today_str})")
    print("="*80)
    
    log_dir = Path("logs/robot")
    ng1_events = []
    
    for log_file in log_dir.glob("robot_*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        event = json.loads(line)
                        stream = event.get('stream', event.get('stream_id', ''))
                        trading_date = event.get('trading_date', '')
                        
                        if stream == 'NG1' and trading_date == today_str:
                            ts = parse_timestamp(event.get('ts_utc', event.get('timestamp', '')))
                            if ts and start_utc <= ts <= end_utc:
                                ng1_events.append((ts, event))
                    except:
                        pass
        except:
            pass
    
    ng1_events.sort(key=lambda x: x[0])
    
    print(f"\nFound {len(ng1_events)} NG1 events in 07:00-08:00 window")
    
    # Group by event type
    event_types = {}
    for ts, event in ng1_events:
        event_type = event.get('event_type', event.get('EventType', 'UNKNOWN'))
        if event_type not in event_types:
            event_types[event_type] = []
        event_types[event_type].append((ts, event))
    
    print("\nEvents by type:")
    for event_type, events in sorted(event_types.items()):
        print(f"  {event_type}: {len(events)} events")
    
    # Show key events
    print("\n" + "="*80)
    print("KEY EVENTS (RANGE_LOCKED, STOP_BRACKETS, STREAM_CREATED)")
    print("="*80)
    
    key_types = ['RANGE_LOCKED', 'STOP_BRACKETS', 'STREAM_CREATED', 'STREAM_INITIALIZED', 'SLOT_TIME']
    for ts, event in ng1_events:
        event_type = event.get('event_type', event.get('EventType', ''))
        if any(k in event_type for k in key_types):
            ts_chicago = ts.astimezone(chicago_tz)
            slot_time = event.get('slot_time_chicago', event.get('SlotTimeChicago', ''))
            data = event.get('data', {})
            if isinstance(data, dict):
                slot_from_data = data.get('slot_time_chicago', '')
            else:
                slot_from_data = ''
            
            slot_display = slot_time or slot_from_data or 'N/A'
            print(f"\n{ts_chicago.strftime('%H:%M:%S')} ({ts.strftime('%H:%M:%S')} UTC) | {event_type}")
            print(f"  Slot time: {slot_display}")
            if isinstance(data, dict):
                for key in ['stream_id', 'state', 'slot_time_chicago', 'slot_time_utc']:
                    if key in data:
                        print(f"  {key}: {data[key]}")
    
    # Check for any 07:30 mentions
    print("\n" + "="*80)
    print("EVENTS WITH 07:30 MENTIONED")
    print("="*80)
    
    for ts, event in ng1_events:
        event_str = json.dumps(event)
        if '07:30' in event_str:
            ts_chicago = ts.astimezone(chicago_tz)
            event_type = event.get('event_type', event.get('EventType', ''))
            print(f"\n{ts_chicago.strftime('%H:%M:%S')} | {event_type}")
            # Find where 07:30 appears
            if 'slot_time_chicago' in event_str:
                print(f"  slot_time_chicago: {event.get('slot_time_chicago', event.get('data', {}).get('slot_time_chicago', ''))}")
    
    return 0

if __name__ == '__main__':
    sys.exit(main())
