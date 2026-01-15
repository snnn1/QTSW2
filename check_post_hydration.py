#!/usr/bin/env python3
"""Check what happens after PRE_HYDRATION_COMPLETE"""
import json
import glob
from datetime import datetime, timezone

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("POST-HYDRATION ANALYSIS")
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
    
    # Find last PRE_HYDRATION_COMPLETE
    last_pre_hydration = None
    for ts, entry in reversed(today_events):
        if entry.get('event') == 'PRE_HYDRATION_COMPLETE':
            last_pre_hydration = ts
            break
    
    if not last_pre_hydration:
        print("\n[ERROR] No PRE_HYDRATION_COMPLETE found today")
        return
    
    print(f"\nLast PRE_HYDRATION_COMPLETE: {last_pre_hydration.strftime('%Y-%m-%d %H:%M:%S')} UTC")
    print(f"Current time: {datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M:%S')} UTC")
    
    time_diff = (datetime.now(timezone.utc) - last_pre_hydration).total_seconds() / 60
    print(f"Time since last PRE_HYDRATION_COMPLETE: {time_diff:.1f} minutes")
    
    # Show events after last PRE_HYDRATION_COMPLETE
    post_hydration_events = [(ts, e) for ts, e in today_events if ts >= last_pre_hydration]
    
    print(f"\nEvents after PRE_HYDRATION_COMPLETE: {len(post_hydration_events)}")
    
    if len(post_hydration_events) <= 1:
        print("\n[CRITICAL] No events after PRE_HYDRATION_COMPLETE!")
        print("           This suggests Tick() is not being called or robot stopped")
    else:
        print("\nFirst 20 events after PRE_HYDRATION_COMPLETE:")
        for ts, entry in post_hydration_events[:20]:
            event = entry.get('event', '')
            state = entry.get('state', '')
            data = entry.get('data', {})
            instrument = data.get('instrument', '')
            session = data.get('session', '')
            stream = data.get('stream', '')
            
            print(f"  [{ts.strftime('%H:%M:%S')}] {event} | State: {state} | {instrument} {stream} {session}")
    
    # Check for ARMED state transitions
    armed_after = [(ts, e) for ts, e in post_hydration_events if e.get('event') == 'ARMED' or e.get('state') == 'ARMED']
    if armed_after:
        print(f"\n[OK] Found {len(armed_after)} ARMED events after PRE_HYDRATION_COMPLETE")
    else:
        print("\n[WARNING] No ARMED events after PRE_HYDRATION_COMPLETE")
    
    # Check for RANGE_WINDOW_STARTED
    range_started_after = [(ts, e) for ts, e in post_hydration_events if e.get('event') == 'RANGE_WINDOW_STARTED']
    if range_started_after:
        print(f"\n[OK] Found {len(range_started_after)} RANGE_WINDOW_STARTED events after PRE_HYDRATION_COMPLETE")
    else:
        print("\n[WARNING] No RANGE_WINDOW_STARTED events after PRE_HYDRATION_COMPLETE")

if __name__ == '__main__':
    main()
