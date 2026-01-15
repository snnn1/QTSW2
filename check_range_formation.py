#!/usr/bin/env python3
"""Check if ranges have formed properly and look for errors"""
import json
import glob
from datetime import datetime, timezone
from collections import defaultdict

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    today = '2026-01-15'
    
    print("=" * 80)
    print(f"RANGE FORMATION CHECK - TODAY ({today})")
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
    s1_events = []
    for entry in all_events:
        try:
            data = entry.get('data', {})
            trading_date = data.get('trading_date', '')
            session = data.get('session', '')
            
            if trading_date == today and session == 'S1':
                ts_str = entry.get('ts_utc', '')
                if ts_str:
                    if ts_str.endswith('Z'):
                        ts_str = ts_str[:-1] + '+00:00'
                    ts = datetime.fromisoformat(ts_str)
                    s1_events.append((ts, entry))
        except:
            continue
    
    s1_events.sort(key=lambda x: x[0])
    
    print(f"\nTotal S1 events for today: {len(s1_events)}")
    
    # Check for range computation events
    print("\n" + "=" * 80)
    print("RANGE COMPUTATION STATUS")
    print("=" * 80)
    
    range_success = []
    range_failed = []
    range_locked = []
    
    for ts, entry in s1_events:
        event_type = entry.get('event', '')
        if 'RANGE_COMPUTE_SUCCESS' in event_type:
            range_success.append((ts, entry))
        elif 'RANGE_COMPUTE_FAILED' in event_type:
            range_failed.append((ts, entry))
        elif 'RANGE_LOCKED' in event_type:
            range_locked.append((ts, entry))
    
    print(f"\nRANGE_COMPUTE_SUCCESS events: {len(range_success)}")
    print(f"RANGE_COMPUTE_FAILED events: {len(range_failed)}")
    print(f"RANGE_LOCKED events: {len(range_locked)}")
    
    # Show successful range computations
    if range_success:
        print("\n" + "=" * 80)
        print("SUCCESSFUL RANGE COMPUTATIONS")
        print("=" * 80)
        for ts, entry in range_success:
            data = entry.get('data', {})
            payload = data.get('payload', {})
            slot_time = data.get('slot_time_chicago', '')
            
            print(f"\n[{ts.strftime('%H:%M:%S UTC')}] Slot: {slot_time}")
            if 'range_high' in payload:
                print(f"  High: {payload.get('range_high')}")
                print(f"  Low: {payload.get('range_low')}")
                print(f"  Freeze Close: {payload.get('freeze_close')}")
                if payload.get('range_high') and payload.get('range_low'):
                    range_size = payload.get('range_high') - payload.get('range_low')
                    print(f"  Range Size: {range_size:.2f} points")
    
    # Show failed range computations
    if range_failed:
        print("\n" + "=" * 80)
        print("FAILED RANGE COMPUTATIONS")
        print("=" * 80)
        for ts, entry in range_failed:
            data = entry.get('data', {})
            payload = data.get('payload', {})
            slot_time = data.get('slot_time_chicago', '')
            reason = payload.get('reason', 'No reason provided')
            
            print(f"\n[{ts.strftime('%H:%M:%S UTC')}] Slot: {slot_time}")
            print(f"  Reason: {reason}")
    
    # Check for errors
    print("\n" + "=" * 80)
    print("ERROR CHECK")
    print("=" * 80)
    
    errors = []
    for ts, entry in s1_events:
        event_type = entry.get('event', '')
        level = entry.get('level', '')
        
        if 'ERROR' in event_type or 'FAILED' in event_type or level == 'ERROR':
            errors.append((ts, entry))
    
    print(f"\nTotal error events: {len(errors)}")
    
    if errors:
        print("\nRecent errors:")
        for ts, entry in errors[-20:]:
            event_type = entry.get('event', 'UNKNOWN')
            data = entry.get('data', {})
            payload = data.get('payload', {})
            slot_time = data.get('slot_time_chicago', '')
            
            print(f"\n[{ts.strftime('%H:%M:%S UTC')}] {event_type}")
            print(f"  Slot: {slot_time}")
            if 'reason' in payload:
                print(f"  Reason: {payload.get('reason')}")
            if 'error' in payload:
                print(f"  Error: {payload.get('error')}")
    else:
        print("\n[OK] No errors found!")
    
    # Check current stream states
    print("\n" + "=" * 80)
    print("CURRENT STREAM STATES")
    print("=" * 80)
    
    streams = defaultdict(lambda: {
        'latest_state': None,
        'latest_event': None,
        'latest_ts': None,
        'range_high': None,
        'range_low': None,
        'freeze_close': None
    })
    
    for ts, entry in s1_events:
        data = entry.get('data', {})
        slot_time = data.get('slot_time_chicago', '')
        
        stream_info = streams[slot_time]
        if stream_info['latest_ts'] is None or ts > stream_info['latest_ts']:
            stream_info['latest_ts'] = ts
            stream_info['latest_event'] = entry.get('event', '')
            stream_info['latest_state'] = data.get('state', '')
        
        payload = data.get('payload', {})
        if 'range_high' in payload:
            stream_info['range_high'] = payload.get('range_high')
        if 'range_low' in payload:
            stream_info['range_low'] = payload.get('range_low')
        if 'freeze_close' in payload:
            stream_info['freeze_close'] = payload.get('freeze_close')
    
    for slot_time in sorted(streams.keys()):
        info = streams[slot_time]
        print(f"\nSlot: {slot_time}")
        print(f"  State: {info['latest_state'] or '[EMPTY]'}")
        print(f"  Latest Event: {info['latest_event']}")
        if info['latest_ts']:
            age_minutes = (datetime.now(timezone.utc) - info['latest_ts']).total_seconds() / 60
            print(f"  Last Update: {info['latest_ts'].strftime('%H:%M:%S UTC')} ({age_minutes:.1f} min ago)")
        
        if info['range_high'] is not None:
            print(f"  Range High: {info['range_high']}")
            print(f"  Range Low: {info['range_low']}")
            print(f"  Freeze Close: {info['freeze_close']}")
            if info['range_high'] and info['range_low']:
                range_size = info['range_high'] - info['range_low']
                print(f"  Range Size: {range_size:.2f} points")
            print(f"  [OK] Range has been computed!")
        else:
            print(f"  [PENDING] Range not computed yet")

if __name__ == '__main__':
    main()
