#!/usr/bin/env python3
"""Check when S1 streams should enter RANGE_BUILDING"""
import json
import glob
from datetime import datetime, timezone
import pytz

def main():
    # S1 configuration
    range_start_chicago = "02:00"  # From config
    slot_time_chicago = "09:00"    # Example slot time
    
    # Get current time
    now_utc = datetime.now(timezone.utc)
    chicago_tz = pytz.timezone("America/Chicago")
    now_chicago = now_utc.astimezone(chicago_tz)
    
    print("=" * 80)
    print("S1 RANGE BUILDING TIMING")
    print("=" * 80)
    
    print(f"\nCurrent Time:")
    print(f"  UTC: {now_utc.strftime('%Y-%m-%d %H:%M:%S %Z')}")
    print(f"  Chicago: {now_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
    
    # Calculate range start time for today
    today = now_chicago.date()
    range_start_time = datetime.strptime(range_start_chicago, "%H:%M").time()
    range_start_chicago_dt = chicago_tz.localize(datetime.combine(today, range_start_time))
    range_start_utc = range_start_chicago_dt.astimezone(timezone.utc)
    
    print(f"\nS1 Range Building Start:")
    print(f"  Chicago Time: {range_start_chicago_dt.strftime('%Y-%m-%d %H:%M:%S %Z')}")
    print(f"  UTC Time: {range_start_utc.strftime('%Y-%m-%d %H:%M:%S %Z')}")
    
    # Check if we're past range start
    time_until_start = (range_start_utc - now_utc).total_seconds() / 60
    if time_until_start < 0:
        print(f"\n[STATUS] Range start time has PASSED ({abs(time_until_start):.1f} minutes ago)")
        print("         S1 streams SHOULD be in RANGE_BUILDING state")
    else:
        print(f"\n[STATUS] Range start time is in the FUTURE ({time_until_start:.1f} minutes)")
        print("         S1 streams SHOULD be in ARMED state (waiting)")
    
    # Check logs for S1 streams
    print("\n" + "=" * 80)
    print("S1 STREAM STATE FROM LOGS")
    print("=" * 80)
    
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    s1_events = []
    
    for log_file in log_files:
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        entry = json.loads(line)
                        data = entry.get('data', {})
                        session = data.get('session', '')
                        if session == 'S1':
                            # Get recent events
                            ts_str = entry.get('ts_utc', '')
                            if ts_str:
                                if ts_str.endswith('Z'):
                                    ts_str = ts_str[:-1] + '+00:00'
                                ts = datetime.fromisoformat(ts_str)
                                if ts >= now_utc.replace(hour=0, minute=0, second=0):
                                    s1_events.append((ts, entry))
                    except:
                        continue
        except:
            continue
    
    if s1_events:
        # Sort by time
        s1_events.sort(key=lambda x: x[0])
        
        # Find latest state transitions
        print("\nRecent S1 state events (last 20):")
        for ts, entry in s1_events[-20:]:
            event = entry.get('event', 'UNKNOWN')
            state = entry.get('state', '')
            instrument = entry.get('data', {}).get('instrument', '')
            stream = entry.get('data', {}).get('stream', '')
            
            # Check for RANGE_WINDOW_STARTED
            if event == 'RANGE_WINDOW_STARTED':
                print(f"  [{ts.strftime('%H:%M:%S')}] {instrument} {stream}: {event} -> {state}")
            elif event in ['ARMED', 'PRE_HYDRATION_COMPLETE', 'RANGE_BUILDING', 'RANGE_LOCKED']:
                print(f"  [{ts.strftime('%H:%M:%S')}] {instrument} {stream}: {event} (state: {state})")
        
        # Check current state
        print("\nCurrent S1 stream states:")
        streams_state = {}
        for ts, entry in s1_events:
            instrument = entry.get('data', {}).get('instrument', '')
            stream = entry.get('data', {}).get('stream', '')
            state = entry.get('state', '')
            key = f"{instrument}_{stream}"
            # Keep latest state
            if key not in streams_state or ts > streams_state[key][0]:
                streams_state[key] = (ts, state, entry.get('event', ''))
        
        for key, (ts, state, event) in sorted(streams_state.items()):
            print(f"  {key}: {state} (last event: {event} at {ts.strftime('%H:%M:%S')})")
        
        # Check if any should be in RANGE_BUILDING
        if time_until_start < 0:
            print("\n[CHECK] Streams that should be in RANGE_BUILDING:")
            for key, (ts, state, event) in sorted(streams_state.items()):
                if state != 'RANGE_BUILDING' and state != 'RANGE_LOCKED' and state != 'DONE':
                    print(f"  [WARNING] {key}: Currently {state}, should be RANGE_BUILDING")
    else:
        print("\nNo S1 events found in logs today")

if __name__ == '__main__':
    main()
