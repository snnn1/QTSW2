#!/usr/bin/env python3
"""Check for range computation events"""
import json
import glob
from datetime import datetime, timezone

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    today = '2026-01-15'
    
    print("=" * 80)
    print(f"RANGE COMPUTATION EVENTS - TODAY ({today})")
    print("=" * 80)
    
    # Get all events
    all_events = []
    for log_file in log_files:
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        entry = json.loads(line)
                        all_events.append(entry)
                    except:
                        continue
        except:
            continue
    
    # Find today's S1 range events
    range_events = []
    for entry in all_events:
        try:
            data = entry.get('data', {})
            trading_date = data.get('trading_date', '')
            session = data.get('session', '')
            event_type = entry.get('event', '')
            
            if trading_date == today and session == 'S1':
                if 'RANGE_COMPUTE' in event_type or 'RANGE_LOCKED' in event_type:
                    ts_str = entry.get('ts_utc', '')
                    if ts_str:
                        if ts_str.endswith('Z'):
                            ts_str = ts_str[:-1] + '+00:00'
                        ts = datetime.fromisoformat(ts_str)
                        range_events.append((ts, entry))
        except:
            continue
    
    range_events.sort(key=lambda x: x[0])
    
    print(f"\nFound {len(range_events)} range computation events for today\n")
    
    if range_events:
        print("Recent range computation events:")
        for ts, entry in range_events[-10:]:
            event_type = entry.get('event', 'UNKNOWN')
            data = entry.get('data', {})
            payload = data.get('payload', {})
            slot_time = data.get('slot_time_chicago', '')
            
            print(f"\n[{ts.strftime('%H:%M:%S UTC')}] {event_type}")
            print(f"  Slot Time: {slot_time}")
            if 'range_high' in payload:
                print(f"  High: {payload.get('range_high')}")
                print(f"  Low: {payload.get('range_low')}")
                print(f"  Freeze Close: {payload.get('freeze_close')}")
            if 'reason' in payload:
                print(f"  Reason: {payload.get('reason')}")
    else:
        print("No range computation events found for today")
        print("This is normal if streams are still in RANGE_BUILDING state")

if __name__ == '__main__':
    main()
