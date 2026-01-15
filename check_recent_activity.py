#!/usr/bin/env python3
"""Check very recent activity to see what's happening right now"""
import json
import glob
from datetime import datetime, timezone, timedelta

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("RECENT ACTIVITY CHECK (Last 2 minutes)")
    print("=" * 80)
    
    now_utc = datetime.now(timezone.utc)
    two_minutes_ago = now_utc - timedelta(minutes=2)
    
    print(f"\nCurrent Time: {now_utc.strftime('%Y-%m-%d %H:%M:%S')} UTC")
    print(f"Checking events since: {two_minutes_ago.strftime('%H:%M:%S')} UTC")
    
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
                if ts >= two_minutes_ago:
                    recent_events.append((ts, entry))
        except:
            continue
    
    recent_events.sort(key=lambda x: x[0])
    
    print(f"\nFound {len(recent_events)} events in last 2 minutes")
    
    if len(recent_events) == 0:
        print("\n[WARNING] No events in last 2 minutes - robot may not be running")
        return
    
    # Group by event type
    event_types = {}
    for ts, entry in recent_events:
        event = entry.get('event', 'UNKNOWN')
        if event not in event_types:
            event_types[event] = []
        event_types[event].append((ts, entry))
    
    print("\nEvents by type:")
    for event_type, events in sorted(event_types.items(), key=lambda x: len(x[1]), reverse=True):
        print(f"  {event_type}: {len(events)} occurrences")
    
    # Show latest events
    print("\n" + "=" * 80)
    print("LATEST 20 EVENTS")
    print("=" * 80)
    for ts, entry in recent_events[-20:]:
        event = entry.get('event', '')
        state = entry.get('state', '')
        data = entry.get('data', {})
        instrument = data.get('instrument', '')
        session = data.get('session', '')
        stream = data.get('stream', '')
        
        time_diff = (now_utc - ts).total_seconds()
        print(f"[{ts.strftime('%H:%M:%S')}] ({time_diff:.0f}s ago) {event} | State: {state} | {instrument} {stream} {session}")
    
    # Check for ENGINE_START
    engine_starts = [(ts, e) for ts, e in recent_events if e.get('event') == 'ENGINE_START']
    if engine_starts:
        print(f"\n[OK] ENGINE_START found: {len(engine_starts)} occurrence(s)")
        for ts, entry in engine_starts:
            print(f"  [{ts.strftime('%H:%M:%S')}] ENGINE_START")
    
    # Check for PRE_HYDRATION_COMPLETE
    pre_hydration = [(ts, e) for ts, e in recent_events if e.get('event') == 'PRE_HYDRATION_COMPLETE']
    if pre_hydration:
        print(f"\n[OK] PRE_HYDRATION_COMPLETE found: {len(pre_hydration)} occurrence(s)")
        for ts, entry in pre_hydration[-3:]:
            data = entry.get('data', {})
            print(f"  [{ts.strftime('%H:%M:%S')}] {data.get('instrument', '')} {data.get('stream', '')} {data.get('session', '')}")
    
    # Check for ARMED or RANGE_BUILDING transitions
    armed_events = [(ts, e) for ts, e in recent_events if e.get('event') == 'PRE_HYDRATION_COMPLETE' or e.get('state') == 'ARMED']
    if armed_events:
        print(f"\n[OK] ARMED state events found: {len(armed_events)} occurrence(s)")
    
    range_building = [(ts, e) for ts, e in recent_events if e.get('event') == 'RANGE_WINDOW_STARTED' or e.get('state') == 'RANGE_BUILDING']
    if range_building:
        print(f"\n[OK] RANGE_BUILDING events found: {len(range_building)} occurrence(s)")
        for ts, entry in range_building[-3:]:
            data = entry.get('data', {})
            print(f"  [{ts.strftime('%H:%M:%S')}] {entry.get('event', '')} - {data.get('instrument', '')} {data.get('stream', '')} {data.get('session', '')}")
    
    # Check for ENGINE_TICK_HEARTBEAT (if diagnostic logs enabled)
    tick_heartbeats = [(ts, e) for ts, e in recent_events if e.get('event') == 'ENGINE_TICK_HEARTBEAT']
    if tick_heartbeats:
        print(f"\n[OK] ENGINE_TICK_HEARTBEAT found: {len(tick_heartbeats)} occurrence(s)")
        print("     This confirms Tick() is being called regularly")
    else:
        print(f"\n[INFO] No ENGINE_TICK_HEARTBEAT found (diagnostic logs may be disabled)")

if __name__ == '__main__':
    main()
