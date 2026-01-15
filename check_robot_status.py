#!/usr/bin/env python3
"""Check robot status and recent activity"""
import json
import glob
from datetime import datetime, timezone

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("ROBOT STATUS CHECK")
    print("=" * 80)
    
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
    
    # Get today's events
    today_start = datetime.now(timezone.utc).replace(hour=0, minute=0, second=0, microsecond=0)
    today_events = []
    
    for entry in all_events:
        try:
            ts_str = entry.get('ts_utc', '')
            if ts_str:
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                if ts >= today_start:
                    today_events.append((ts, entry))
        except:
            continue
    
    today_events.sort(key=lambda x: x[0])
    
    print(f"\nFound {len(today_events)} events today")
    
    if not today_events:
        print("\n[CRITICAL] No events found today - Robot may not be running!")
        return
    
    # Check latest event
    latest_ts, latest_entry = today_events[-1]
    print(f"\nLatest Event: {latest_entry.get('event', '')} at {latest_ts.strftime('%Y-%m-%d %H:%M:%S')} UTC")
    print(f"Latest State: {latest_entry.get('state', '')}")
    
    # Check for ENGINE_START
    engine_starts = [(ts, e) for ts, e in today_events if e.get('event') == 'ENGINE_START']
    if engine_starts:
        print(f"\n[OK] ENGINE_START found: {len(engine_starts)} occurrence(s)")
        for ts, entry in engine_starts:
            print(f"  [{ts.strftime('%H:%M:%S')}] ENGINE_START")
    else:
        print("\n[WARNING] No ENGINE_START found today")
    
    # Check for ENGINE_STOP
    engine_stops = [(ts, e) for ts, e in today_events if e.get('event') == 'ENGINE_STOP']
    if engine_stops:
        print(f"\n[WARNING] ENGINE_STOP found: {len(engine_stops)} occurrence(s)")
        for ts, entry in engine_stops:
            print(f"  [{ts.strftime('%H:%M:%S')}] ENGINE_STOP")
    
    # Check for PRE_HYDRATION_COMPLETE
    pre_hydration = [(ts, e) for ts, e in today_events if e.get('event') == 'PRE_HYDRATION_COMPLETE']
    if pre_hydration:
        print(f"\n[OK] PRE_HYDRATION_COMPLETE found: {len(pre_hydration)} occurrence(s)")
        for ts, entry in pre_hydration[-3:]:
            data = entry.get('data', {})
            print(f"  [{ts.strftime('%H:%M:%S')}] {data.get('instrument', '')} {data.get('stream', '')} {data.get('session', '')}")
    
    # Check for ARMED
    armed_events = [(ts, e) for ts, e in today_events if e.get('event') == 'ARMED' or e.get('state') == 'ARMED']
    if armed_events:
        print(f"\n[OK] ARMED events found: {len(armed_events)} occurrence(s)")
        for ts, entry in armed_events[-3:]:
            data = entry.get('data', {})
            print(f"  [{ts.strftime('%H:%M:%S')}] {entry.get('event', '')} - {data.get('instrument', '')} {data.get('stream', '')} {data.get('session', '')}")
    
    # Check for RANGE_WINDOW_STARTED
    range_started = [(ts, e) for ts, e in today_events if e.get('event') == 'RANGE_WINDOW_STARTED']
    if range_started:
        print(f"\n[OK] RANGE_WINDOW_STARTED found: {len(range_started)} occurrence(s)")
        for ts, entry in range_started[-3:]:
            data = entry.get('data', {})
            print(f"  [{ts.strftime('%H:%M:%S')}] {data.get('instrument', '')} {data.get('stream', '')} {data.get('session', '')}")
    else:
        print("\n[WARNING] No RANGE_WINDOW_STARTED found today")
    
    # Check for any bar events
    bar_events = [(ts, e) for ts, e in today_events if 'BAR' in e.get('event', '')]
    if bar_events:
        print(f"\n[OK] Bar events found: {len(bar_events)} occurrence(s)")
    else:
        print("\n[WARNING] No bar events found today")
    
    # Show last 10 events
    print("\n" + "=" * 80)
    print("LAST 10 EVENTS")
    print("=" * 80)
    for ts, entry in today_events[-10:]:
        event = entry.get('event', '')
        state = entry.get('state', '')
        data = entry.get('data', {})
        instrument = data.get('instrument', '')
        session = data.get('session', '')
        print(f"[{ts.strftime('%H:%M:%S')}] {event} | State: {state} | {instrument} {session}")

if __name__ == '__main__':
    main()
