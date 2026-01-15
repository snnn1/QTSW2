#!/usr/bin/env python3
"""Get detailed S1 stream state and range information"""
import json
import glob
from datetime import datetime, timezone
from collections import defaultdict

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("S1 STREAM STATE & RANGE DETAILS")
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
    
    # Find S1 events
    s1_events = []
    for entry in all_events:
        try:
            data = entry.get('data', {})
            session = data.get('session', '')
            if session == 'S1':
                ts_str = entry.get('ts_utc', '')
                if ts_str:
                    if ts_str.endswith('Z'):
                        ts_str = ts_str[:-1] + '+00:00'
                    ts = datetime.fromisoformat(ts_str)
                    s1_events.append((ts, entry))
        except:
            continue
    
    s1_events.sort(key=lambda x: x[0])
    
    # Group by stream (use trading_date + slot_time as key)
    streams = defaultdict(lambda: {
        'latest_state': None,
        'latest_event': None,
        'latest_ts': None,
        'range_high': None,
        'range_low': None,
        'freeze_close': None,
        'range_start_time': None,
        'range_end_time': None,
        'trading_date': None,
        'slot_time': None,
        'instrument': None
    })
    
    for ts, entry in s1_events:
        data = entry.get('data', {})
        trading_date = data.get('trading_date', '')
        slot_time = data.get('slot_time_chicago', '')
        stream_key = f"{trading_date}_{slot_time}"
        
        # Update stream info
        stream_info = streams[stream_key]
        if stream_info['latest_ts'] is None or ts > stream_info['latest_ts']:
            stream_info['latest_ts'] = ts
            stream_info['latest_event'] = entry.get('event', '')
            stream_info['latest_state'] = data.get('state', '')
            stream_info['trading_date'] = trading_date
            stream_info['slot_time'] = slot_time
            stream_info['instrument'] = data.get('instrument', 'UNKNOWN')
        
        # Extract range information from various event types
        payload = data.get('payload', {})
        event_type = entry.get('event', '')
        
        if 'range_high' in payload:
            stream_info['range_high'] = payload.get('range_high')
        if 'range_low' in payload:
            stream_info['range_low'] = payload.get('range_low')
        if 'freeze_close' in payload:
            stream_info['freeze_close'] = payload.get('freeze_close')
        if 'range_start_time' in payload:
            stream_info['range_start_time'] = payload.get('range_start_time')
        if 'range_end_time' in payload:
            stream_info['range_end_time'] = payload.get('range_end_time')
    
    # Display current streams
    print(f"\nFound {len(streams)} S1 streams\n")
    
    for stream_key, info in sorted(streams.items(), key=lambda x: x[1]['slot_time'] or ''):
        print("=" * 80)
        print(f"Stream: {info['instrument']} | Trading Date: {info['trading_date']} | Slot: {info['slot_time']}")
        print("=" * 80)
        print(f"  Current State: {info['latest_state'] or '[EMPTY]'}")
        print(f"  Latest Event: {info['latest_event']}")
        if info['latest_ts']:
            age_minutes = (datetime.now(timezone.utc) - info['latest_ts']).total_seconds() / 60
            print(f"  Last Update: {info['latest_ts'].strftime('%H:%M:%S UTC')} ({age_minutes:.1f} minutes ago)")
        
        print(f"\n  Range Information:")
        if info['range_high'] is not None:
            print(f"    High: {info['range_high']}")
        else:
            print(f"    High: [Not computed yet]")
            
        if info['range_low'] is not None:
            print(f"    Low: {info['range_low']}")
        else:
            print(f"    Low: [Not computed yet]")
            
        if info['freeze_close'] is not None:
            print(f"    Freeze Close: {info['freeze_close']}")
        else:
            print(f"    Freeze Close: [Not computed yet]")
        
        if info['range_start_time']:
            print(f"    Range Start: {info['range_start_time']}")
        if info['range_end_time']:
            print(f"    Range End: {info['range_end_time']}")
        
        # State interpretation
        state = info['latest_state']
        if state == 'RANGE_BUILDING':
            print(f"\n  [STATUS] Stream is actively building range from live bars")
        elif state == 'RANGE_LOCKED':
            print(f"\n  [STATUS] Range is locked - trading can begin")
        elif state == 'ARMED':
            print(f"\n  [STATUS] Stream is armed, waiting for range start time")
        elif state == 'PRE_HYDRATION':
            print(f"\n  [STATUS] Loading historical bars for pre-hydration")
        elif state == 'DONE':
            print(f"\n  [STATUS] Stream completed for the day")
        else:
            print(f"\n  [STATUS] State: {state}")
    
    # Find recent range computation events
    print("\n" + "=" * 80)
    print("RECENT RANGE COMPUTATION EVENTS (S1)")
    print("=" * 80)
    
    range_compute_events = []
    for ts, entry in s1_events[-500:]:  # Last 500 events
        event_type = entry.get('event', '')
        if 'RANGE_COMPUTE' in event_type or 'RANGE_LOCKED' in event_type:
            range_compute_events.append((ts, entry))
    
    if range_compute_events:
        print(f"\nFound {len(range_compute_events)} range computation events")
        print("\nLast 5 range computation events:")
        for ts, entry in range_compute_events[-5:]:
            event_type = entry.get('event', 'UNKNOWN')
            data = entry.get('data', {})
            payload = data.get('payload', {})
            slot_time = data.get('slot_time_chicago', '')
            
            print(f"\n[{ts.strftime('%H:%M:%S')}] {event_type} | Slot: {slot_time}")
            if 'range_high' in payload:
                print(f"  High: {payload.get('range_high')}, Low: {payload.get('range_low')}, Freeze Close: {payload.get('freeze_close')}")
            if 'reason' in payload:
                print(f"  Reason: {payload.get('reason')}")
    else:
        print("\nNo recent range computation events found")

if __name__ == '__main__':
    main()
