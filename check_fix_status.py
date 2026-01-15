#!/usr/bin/env python3
"""Check if the rollover spam fix is working"""
import json
import glob
from datetime import datetime, timezone, timedelta
from collections import Counter

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("FIX VERIFICATION")
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
    
    print(f"\nCurrent Time: {now_utc.strftime('%Y-%m-%d %H:%M:%S')} UTC")
    print(f"Checking events since: {five_minutes_ago.strftime('%H:%M:%S')} UTC")
    print(f"\nFound {len(recent_events)} events in last 5 minutes")
    
    # Check for rollover events
    rollover_events = [(ts, e) for ts, e in recent_events if e.get('event') == 'TRADING_DAY_ROLLOVER']
    initialized_events = [(ts, e) for ts, e in recent_events if e.get('event') == 'TRADING_DATE_INITIALIZED']
    
    print(f"\n" + "=" * 80)
    print("ROLLOVER SPAM CHECK")
    print("=" * 80)
    print(f"TRADING_DAY_ROLLOVER events: {len(rollover_events)}")
    print(f"TRADING_DATE_INITIALIZED events: {len(initialized_events)}")
    
    if len(rollover_events) > 100:
        print(f"\n[ISSUE] Still seeing {len(rollover_events)} rollover events - fix may not be working")
    elif len(rollover_events) > 10:
        print(f"\n[WARNING] {len(rollover_events)} rollover events - may still be an issue")
    else:
        print(f"\n[OK] Rollover spam appears to be fixed ({len(rollover_events)} events)")
    
    if initialized_events:
        print(f"\n[OK] Found {len(initialized_events)} TRADING_DATE_INITIALIZED events")
        print("     This confirms the fix is working")
    
    # Check for stream transitions
    print(f"\n" + "=" * 80)
    print("STREAM TRANSITIONS")
    print("=" * 80)
    
    pre_hydration = [(ts, e) for ts, e in recent_events if e.get('event') == 'PRE_HYDRATION_COMPLETE']
    armed = [(ts, e) for ts, e in recent_events if e.get('event') == 'STREAM_ARMED' or e.get('state') == 'ARMED']
    range_started = [(ts, e) for ts, e in recent_events if e.get('event') == 'RANGE_WINDOW_STARTED']
    range_building = [(ts, e) for ts, e in recent_events if e.get('state') == 'RANGE_BUILDING']
    
    print(f"PRE_HYDRATION_COMPLETE: {len(pre_hydration)}")
    print(f"ARMED: {len(armed)}")
    print(f"RANGE_WINDOW_STARTED: {len(range_started)}")
    print(f"RANGE_BUILDING state: {len(range_building)}")
    
    if range_started:
        print(f"\n[SUCCESS] Found {len(range_started)} RANGE_WINDOW_STARTED events!")
        print("          Streams are transitioning to RANGE_BUILDING")
        print("\n  Recent RANGE_WINDOW_STARTED events:")
        for ts, entry in range_started[-5:]:
            data = entry.get('data', {})
            print(f"    [{ts.strftime('%H:%M:%S')}] {data.get('instrument', '')} {data.get('stream', '')} {data.get('session', '')}")
    else:
        print(f"\n[WARNING] No RANGE_WINDOW_STARTED events found")
        print("          Streams may not be transitioning to RANGE_BUILDING")
    
    # Check S1 stream status
    print(f"\n" + "=" * 80)
    print("S1 STREAM STATUS")
    print("=" * 80)
    
    s1_events = [(ts, e) for ts, e in recent_events if e.get('data', {}).get('session') == 'S1']
    
    if s1_events:
        # Find latest state for S1
        s1_states = {}
        for ts, entry in s1_events:
            stream = entry.get('data', {}).get('stream', 'UNKNOWN')
            state = entry.get('state', '')
            event = entry.get('event', '')
            
            if stream not in s1_states:
                s1_states[stream] = {'latest_ts': ts, 'latest_state': state, 'latest_event': event}
            elif ts > s1_states[stream]['latest_ts']:
                s1_states[stream]['latest_ts'] = ts
                s1_states[stream]['latest_state'] = state
                s1_states[stream]['latest_event'] = event
        
        for stream, info in s1_states.items():
            print(f"\n  Stream: {stream}")
            print(f"    Latest State: {info['latest_state']}")
            print(f"    Latest Event: {info['latest_event']}")
            print(f"    Last Update: {info['latest_ts'].strftime('%H:%M:%S')}")
            
            if info['latest_state'] == 'RANGE_BUILDING' or info['latest_state'] == 'RANGE_LOCKED':
                print(f"    [OK] Stream is in active trading state")
            elif info['latest_state'] == 'ARMED':
                print(f"    [INFO] Stream is ARMED, waiting for range start time")
            else:
                print(f"    [WARNING] Stream state: {info['latest_state']}")
    
    # Show latest events
    print(f"\n" + "=" * 80)
    print("LATEST 15 EVENTS")
    print("=" * 80)
    for ts, entry in recent_events[-15:]:
        event = entry.get('event', '')
        state = entry.get('state', '')
        data = entry.get('data', {})
        instrument = data.get('instrument', '')
        session = data.get('session', '')
        
        time_diff = (now_utc - ts).total_seconds()
        print(f"[{ts.strftime('%H:%M:%S')}] ({time_diff:.0f}s ago) {event} | State: {state} | {instrument} {session}")

if __name__ == '__main__':
    main()
