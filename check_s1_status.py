#!/usr/bin/env python3
"""Check S1 stream state and range information"""
import json
import glob
from datetime import datetime, timezone

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("S1 STREAM STATUS & RANGE INFORMATION")
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
    
    # Find S1 stream events
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
    
    print(f"\nTotal S1 events found: {len(s1_events)}")
    
    if not s1_events:
        print("\n[WARNING] No S1 events found")
        return
    
    # Get latest state information
    print("\n" + "=" * 80)
    print("LATEST S1 STREAM STATES")
    print("=" * 80)
    
    # Group by stream identifier (instrument + slot_time)
    streams = {}
    for ts, entry in s1_events:
        data = entry.get('data', {})
        instrument = data.get('instrument', 'UNKNOWN')
        slot_time = data.get('slot_time_chicago', 'UNKNOWN')
        stream_key = f"{instrument}_{slot_time}"
        
        if stream_key not in streams:
            streams[stream_key] = {
                'instrument': instrument,
                'slot_time': slot_time,
                'latest_event': None,
                'latest_state': None,
                'latest_ts': None,
                'range_info': {}
            }
        
        # Update if this is newer
        if streams[stream_key]['latest_ts'] is None or ts > streams[stream_key]['latest_ts']:
            streams[stream_key]['latest_ts'] = ts
            streams[stream_key]['latest_event'] = entry.get('event', 'UNKNOWN')
            streams[stream_key]['latest_state'] = data.get('state', '')
            
            # Extract range information
            payload = data.get('payload', {})
            if 'range_high' in payload or 'range_low' in payload:
                streams[stream_key]['range_info'] = {
                    'high': payload.get('range_high'),
                    'low': payload.get('range_low'),
                    'freeze_close': payload.get('freeze_close'),
                    'range_start': payload.get('range_start_time'),
                    'range_end': payload.get('range_end_time')
                }
    
    # Display stream information
    for stream_key, info in sorted(streams.items(), key=lambda x: x[1]['latest_ts'] or datetime.min.replace(tzinfo=timezone.utc), reverse=True):
        print(f"\nStream: {info['instrument']} | Slot: {info['slot_time']}")
        print(f"  Latest State: {info['latest_state'] or '[EMPTY]'}")
        print(f"  Latest Event: {info['latest_event']}")
        if info['latest_ts']:
            age_minutes = (datetime.now(timezone.utc) - info['latest_ts']).total_seconds() / 60
            print(f"  Last Update: {info['latest_ts'].strftime('%H:%M:%S UTC')} ({age_minutes:.1f} minutes ago)")
        
        if info['range_info']:
            print(f"  Range Information:")
            if info['range_info'].get('high') is not None:
                print(f"    High: {info['range_info']['high']}")
            if info['range_info'].get('low') is not None:
                print(f"    Low: {info['range_info']['low']}")
            if info['range_info'].get('freeze_close') is not None:
                print(f"    Freeze Close: {info['range_info']['freeze_close']}")
            if info['range_info'].get('range_start'):
                print(f"    Range Start: {info['range_info']['range_start']}")
            if info['range_info'].get('range_end'):
                print(f"    Range End: {info['range_info']['range_end']}")
    
    # Find range-related events
    print("\n" + "=" * 80)
    print("RECENT RANGE-RELATED EVENTS (S1)")
    print("=" * 80)
    
    range_events = []
    for ts, entry in s1_events[-100:]:  # Last 100 events
        event_type = entry.get('event', '')
        if any(keyword in event_type for keyword in ['RANGE', 'ARMED', 'PRE_HYDRATION', 'LOCKED', 'DONE']):
            range_events.append((ts, entry))
    
    print(f"\nFound {len(range_events)} range-related events in last 100 S1 events")
    
    if range_events:
        print("\nLast 10 range-related events:")
        for ts, entry in range_events[-10:]:
            event_type = entry.get('event', 'UNKNOWN')
            data = entry.get('data', {})
            state = data.get('state', '')
            payload = data.get('payload', {})
            
            print(f"\n[{ts.strftime('%H:%M:%S')}] {event_type}")
            print(f"  State: {state}")
            
            # Show range values if present
            if 'range_high' in payload:
                print(f"  Range High: {payload.get('range_high')}")
            if 'range_low' in payload:
                print(f"  Range Low: {payload.get('range_low')}")
            if 'freeze_close' in payload:
                print(f"  Freeze Close: {payload.get('freeze_close')}")

if __name__ == '__main__':
    main()
