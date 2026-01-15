#!/usr/bin/env python3
"""Investigate why S1 streams didn't transition to RANGE_BUILDING"""
import json
import glob
from datetime import datetime, timezone
import pytz

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("S1 TRANSITION INVESTIGATION")
    print("=" * 80)
    
    # Get all relevant events
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
    
    # Filter to today and S1-related
    today_start = datetime.now(timezone.utc).replace(hour=0, minute=0, second=0, microsecond=0)
    s1_events = []
    
    for entry in all_events:
        try:
            ts_str = entry.get('ts_utc', '')
            if ts_str:
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                if ts >= today_start:
                    data = entry.get('data', {})
                    session = data.get('session', '')
                    event = entry.get('event', '')
                    
                    # Get S1 events or transition-related events
                    if session == 'S1' or 'ARMED' in event or 'RANGE_BUILD' in event or 'PRE_HYDRATION' in event:
                        s1_events.append((ts, entry))
        except:
            continue
    
    s1_events.sort(key=lambda x: x[0])
    
    print(f"\nFound {len(s1_events)} relevant events today")
    
    # Group by stream
    streams = {}
    for ts, entry in s1_events:
        data = entry.get('data', {})
        instrument = data.get('instrument', 'UNKNOWN')
        stream = data.get('stream', 'UNKNOWN')
        session = data.get('session', '')
        
        if session == 'S1':
            key = f"{instrument}_{stream}"
            if key not in streams:
                streams[key] = {
                    'events': [],
                    'instrument': instrument,
                    'stream': stream
                }
            streams[key]['events'].append((ts, entry))
    
    print(f"\nFound {len(streams)} S1 streams")
    
    # Analyze each stream
    for key, stream_info in sorted(streams.items()):
        print("\n" + "=" * 80)
        print(f"STREAM: {key}")
        print("=" * 80)
        
        events = sorted(stream_info['events'], key=lambda x: x[0])
        
        # Find key events
        pre_hydration_complete = None
        armed_events = []
        range_window_started = None
        armed_diagnostics = []
        
        for ts, entry in events:
            event = entry.get('event', '')
            state = entry.get('state', '')
            
            if event == 'PRE_HYDRATION_COMPLETE':
                pre_hydration_complete = (ts, entry)
            elif event == 'ARMED' or state == 'ARMED':
                armed_events.append((ts, entry))
            elif event == 'RANGE_WINDOW_STARTED':
                range_window_started = (ts, entry)
            elif event == 'ARMED_STATE_DIAGNOSTIC':
                armed_diagnostics.append((ts, entry))
        
        # Show timeline
        print("\nKey Events:")
        if pre_hydration_complete:
            ts, entry = pre_hydration_complete
            print(f"  [{ts.strftime('%H:%M:%S')}] PRE_HYDRATION_COMPLETE")
            print(f"    State: {entry.get('state', '')}")
        
        if armed_events:
            print(f"\n  ARMED events ({len(armed_events)}):")
            for ts, entry in armed_events[:5]:
                print(f"    [{ts.strftime('%H:%M:%S')}] {entry.get('event', '')} - State: {entry.get('state', '')}")
        
        if range_window_started:
            ts, entry = range_window_started
            print(f"\n  [{ts.strftime('%H:%M:%S')}] RANGE_WINDOW_STARTED")
        else:
            print(f"\n  [MISSING] RANGE_WINDOW_STARTED - Never occurred!")
        
        # Check ARMED_STATE_DIAGNOSTIC logs
        if armed_diagnostics:
            print(f"\n  ARMED_STATE_DIAGNOSTIC logs ({len(armed_diagnostics)}):")
            for ts, entry in armed_diagnostics[-5:]:
                payload = entry.get('data', {}).get('payload', {})
                can_transition = payload.get('can_transition', False)
                time_until = payload.get('time_until_range_start_minutes', 0)
                pre_hydration = payload.get('pre_hydration_complete', False)
                
                status = "[CAN TRANSITION]" if can_transition else "[WAITING]"
                print(f"    [{ts.strftime('%H:%M:%S')}] {status}")
                print(f"      can_transition: {can_transition}")
                print(f"      pre_hydration_complete: {pre_hydration}")
                print(f"      time_until_range_start: {time_until:.1f} minutes")
                
                if can_transition and not range_window_started:
                    print(f"      [ISSUE] can_transition=True but never transitioned!")
        
        # Check for ENGINE_TICK_HEARTBEAT
        tick_heartbeats = [e for ts, e in events if e.get('event') == 'ENGINE_TICK_HEARTBEAT']
        if tick_heartbeats:
            print(f"\n  ENGINE_TICK_HEARTBEAT: {len(tick_heartbeats)} occurrences (Tick() is being called)")
        else:
            print(f"\n  [WARNING] No ENGINE_TICK_HEARTBEAT found - Tick() may not be called!")
        
        # Check latest state
        if events:
            latest_ts, latest_entry = events[-1]
            latest_state = latest_entry.get('state', '')
            latest_event = latest_entry.get('event', '')
            print(f"\n  Latest State: {latest_state}")
            print(f"  Latest Event: {latest_event} at {latest_ts.strftime('%H:%M:%S')}")
    
    # Check for any RANGE_WINDOW_STARTED events at all
    print("\n" + "=" * 80)
    print("RANGE_WINDOW_STARTED EVENTS (All Sessions)")
    print("=" * 80)
    
    all_range_started = []
    for entry in all_events:
        if entry.get('event') == 'RANGE_WINDOW_STARTED':
            try:
                ts_str = entry.get('ts_utc', '')
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                data = entry.get('data', {})
                all_range_started.append((ts, entry))
            except:
                pass
    
    if all_range_started:
        print(f"\nFound {len(all_range_started)} RANGE_WINDOW_STARTED events:")
        for ts, entry in sorted(all_range_started, key=lambda x: x[0])[-10:]:
            data = entry.get('data', {})
            print(f"  [{ts.strftime('%Y-%m-%d %H:%M:%S')}] {data.get('instrument', '')} {data.get('stream', '')} {data.get('session', '')}")
    else:
        print("\n[CRITICAL] No RANGE_WINDOW_STARTED events found at all!")
        print("           This suggests streams never transitioned to RANGE_BUILDING")

if __name__ == '__main__':
    main()
