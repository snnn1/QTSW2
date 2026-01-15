#!/usr/bin/env python3
"""Check today's active S1 streams"""
import json
import glob
from datetime import datetime, timezone, date

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    today = date.today().strftime('%Y-%m-%d')
    
    print("=" * 80)
    print(f"S1 STREAM STATUS - TODAY ({today})")
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
    
    # Find today's S1 events
    s1_today = []
    for entry in all_events:
        try:
            data = entry.get('data', {})
            session = data.get('session', '')
            trading_date = data.get('trading_date', '')
            if session == 'S1' and trading_date == today:
                ts_str = entry.get('ts_utc', '')
                if ts_str:
                    if ts_str.endswith('Z'):
                        ts_str = ts_str[:-1] + '+00:00'
                    ts = datetime.fromisoformat(ts_str)
                    s1_today.append((ts, entry))
        except:
            continue
    
    s1_today.sort(key=lambda x: x[0])
    
    if not s1_today:
        print(f"\n[WARNING] No S1 events found for today ({today})")
        return
    
    # Group by slot_time
    streams = {}
    for ts, entry in s1_today:
        data = entry.get('data', {})
        slot_time = data.get('slot_time_chicago', 'UNKNOWN')
        
        if slot_time not in streams:
            streams[slot_time] = {
                'slot_time': slot_time,
                'latest_state': None,
                'latest_event': None,
                'latest_ts': None,
                'range_high': None,
                'range_low': None,
                'freeze_close': None,
                'instrument': data.get('instrument', 'UNKNOWN')
            }
        
        stream_info = streams[slot_time]
        if stream_info['latest_ts'] is None or ts > stream_info['latest_ts']:
            stream_info['latest_ts'] = ts
            stream_info['latest_event'] = entry.get('event', '')
            stream_info['latest_state'] = data.get('state', '')
        
        # Extract range info
        payload = data.get('payload', {})
        if 'range_high' in payload:
            stream_info['range_high'] = payload.get('range_high')
        if 'range_low' in payload:
            stream_info['range_low'] = payload.get('range_low')
        if 'freeze_close' in payload:
            stream_info['freeze_close'] = payload.get('freeze_close')
    
    print(f"\nFound {len(streams)} active S1 streams for today\n")
    
    for slot_time in sorted(streams.keys()):
        info = streams[slot_time]
        print("=" * 80)
        print(f"Slot Time: {slot_time}")
        print("=" * 80)
        print(f"  Instrument: {info['instrument']}")
        print(f"  Current State: {info['latest_state'] or '[EMPTY]'}")
        print(f"  Latest Event: {info['latest_event']}")
        if info['latest_ts']:
            age_minutes = (datetime.now(timezone.utc) - info['latest_ts']).total_seconds() / 60
            print(f"  Last Update: {info['latest_ts'].strftime('%H:%M:%S UTC')} ({age_minutes:.1f} minutes ago)")
        
        print(f"\n  Range Values:")
        if info['range_high'] is not None:
            print(f"    High: {info['range_high']}")
            print(f"    Low: {info['range_low']}")
            print(f"    Freeze Close: {info['freeze_close']}")
            if info['range_high'] and info['range_low']:
                range_size = info['range_high'] - info['range_low']
                print(f"    Range Size: {range_size:.2f} points")
        else:
            print(f"    [Range not computed yet - stream is in {info['latest_state']} state]")
        
        # State details
        state = info['latest_state']
        if state == 'RANGE_BUILDING':
            print(f"\n  [INFO] Stream is actively collecting bars and computing range")
        elif state == 'RANGE_LOCKED':
            print(f"\n  [INFO] Range is locked - ready for trading")
        elif state == 'ARMED':
            print(f"\n  [INFO] Stream is armed, waiting for range start time")
        elif state == 'PRE_HYDRATION':
            print(f"\n  [INFO] Loading historical bars from CSV files")
        print()

if __name__ == '__main__':
    main()
