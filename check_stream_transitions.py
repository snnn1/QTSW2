#!/usr/bin/env python3
"""Check if streams are transitioning properly"""
import json
import glob
from datetime import datetime, timezone, timedelta

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("STREAM TRANSITION CHECK")
    print("=" * 80)
    
    now_utc = datetime.now(timezone.utc)
    five_minutes_ago = now_utc - timedelta(minutes=5)
    
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
    
    # Get recent events
    recent_events = []
    for entry in all_events:
        try:
            ts_str = entry.get('ts_utc', '')
            if ts_str:
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                if ts >= five_minutes_ago:
                    recent_events.append((ts, entry))
        except:
            continue
    
    recent_events.sort(key=lambda x: x[0])
    
    # Find S1 streams and track their state progression
    s1_streams = {}
    
    for ts, entry in recent_events:
        data = entry.get('data', {})
        session = data.get('session', '')
        instrument = data.get('instrument', '')
        stream = data.get('stream', 'UNKNOWN')
        event = entry.get('event', '')
        state = entry.get('state', '')
        
        if session == 'S1':
            key = f"{instrument}_{stream}"
            if key not in s1_streams:
                s1_streams[key] = {
                    'events': [],
                    'states': [],
                    'instrument': instrument,
                    'stream': stream
                }
            
            s1_streams[key]['events'].append((ts, event, state))
            
            if state:
                if not s1_streams[key]['states'] or s1_streams[key]['states'][-1][1] != state:
                    s1_streams[key]['states'].append((ts, state))
    
    print(f"\nFound {len(s1_streams)} S1 streams")
    
    for key, stream_info in sorted(s1_streams.items()):
        print("\n" + "=" * 80)
        print(f"STREAM: {key}")
        print("=" * 80)
        
        # Show state progression
        if stream_info['states']:
            print("\nState Progression:")
            for ts, state in stream_info['states']:
                print(f"  [{ts.strftime('%H:%M:%S')}] {state}")
        else:
            print("\n[WARNING] No state transitions found")
        
        # Check for key events
        events = stream_info['events']
        pre_hydration = [e for ts, e, s in events if e == 'PRE_HYDRATION_COMPLETE']
        armed = [e for ts, e, s in events if e == 'STREAM_ARMED' or s == 'ARMED']
        range_started = [e for ts, e, s in events if e == 'RANGE_WINDOW_STARTED' or s == 'RANGE_BUILDING']
        
        print(f"\nKey Events:")
        print(f"  PRE_HYDRATION_COMPLETE: {len(pre_hydration)}")
        print(f"  ARMED: {len(armed)}")
        print(f"  RANGE_WINDOW_STARTED/RANGE_BUILDING: {len(range_started)}")
        
        # Show latest state
        if stream_info['states']:
            latest_ts, latest_state = stream_info['states'][-1]
            print(f"\n  Latest State: {latest_state} (at {latest_ts.strftime('%H:%M:%S')})")
            
            # Check if it should be in RANGE_BUILDING
            if latest_state != 'RANGE_BUILDING' and latest_state != 'RANGE_LOCKED':
                print(f"  [ISSUE] Should be in RANGE_BUILDING/RANGE_LOCKED (range start was 17+ hours ago)")
        
        # Show last 10 events for this stream
        print(f"\n  Last 10 events:")
        for ts, event, state in events[-10:]:
            time_diff = (now_utc - ts).total_seconds()
            print(f"    [{ts.strftime('%H:%M:%S')}] ({time_diff:.0f}s ago) {event} | State: {state}")
    
    # Check for TRADING_DAY_ROLLOVER spam
    rollover_events = [(ts, entry) for ts, entry in recent_events if entry.get('event') == 'TRADING_DAY_ROLLOVER']
    if len(rollover_events) > 100:
        print("\n" + "=" * 80)
        print(f"[WARNING] Found {len(rollover_events)} TRADING_DAY_ROLLOVER events in last 5 minutes!")
        print("          This suggests a loop or rapid date changes")
        print("          Showing first 5:")
        for ts, entry in rollover_events[:5]:
            data = entry.get('data', {})
            print(f"  [{ts.strftime('%H:%M:%S')}] {data.get('previous_trading_date', '')} -> {data.get('new_trading_date', '')}")

if __name__ == '__main__':
    main()
