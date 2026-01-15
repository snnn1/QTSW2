#!/usr/bin/env python3
"""Check S1 stream states and when they should enter RANGE_BUILDING"""
import json
import glob
from datetime import datetime, timezone
import pytz

def main():
    # S1 configuration
    range_start_chicago = "02:00"
    
    # Current time
    now_utc = datetime.now(timezone.utc)
    chicago_tz = pytz.timezone("America/Chicago")
    now_chicago = now_utc.astimezone(chicago_tz)
    
    # Calculate range start for today
    today = now_chicago.date()
    range_start_time = datetime.strptime(range_start_chicago, "%H:%M").time()
    range_start_chicago_dt = chicago_tz.localize(datetime.combine(today, range_start_time))
    range_start_utc = range_start_chicago_dt.astimezone(timezone.utc)
    
    print("=" * 80)
    print("S1 RANGE BUILDING TIMING")
    print("=" * 80)
    print(f"\nCurrent Time: {now_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')} ({now_utc.strftime('%H:%M:%S UTC')})")
    print(f"S1 Range Start: {range_start_chicago_dt.strftime('%Y-%m-%d %H:%M:%S %Z')} ({range_start_utc.strftime('%H:%M:%S UTC')})")
    
    time_diff = (now_utc - range_start_utc).total_seconds() / 3600
    if time_diff > 0:
        print(f"\n[STATUS] Range start was {time_diff:.1f} hours ago")
        print("         S1 streams SHOULD be in RANGE_BUILDING or RANGE_LOCKED")
    else:
        print(f"\n[STATUS] Range start is in {abs(time_diff):.1f} hours")
        print("         S1 streams SHOULD be in ARMED state")
    
    # Check logs
    print("\n" + "=" * 80)
    print("S1 STREAM STATES FROM LOGS")
    print("=" * 80)
    
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    s1_streams = {}
    
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
                        instrument = data.get('instrument', '')
                        stream = data.get('stream', '')
                        event = entry.get('event', '')
                        state = entry.get('state', '')
                        
                        if session == 'S1':
                            key = f"{instrument}_{stream}"
                            ts_str = entry.get('ts_utc', '')
                            if ts_str:
                                if ts_str.endswith('Z'):
                                    ts_str = ts_str[:-1] + '+00:00'
                                try:
                                    ts = datetime.fromisoformat(ts_str)
                                    # Keep latest event per stream
                                    if key not in s1_streams or ts > s1_streams[key]['ts']:
                                        s1_streams[key] = {
                                            'ts': ts,
                                            'event': event,
                                            'state': state,
                                            'instrument': instrument,
                                            'stream': stream
                                        }
                                except:
                                    pass
                    except:
                        continue
        except:
            continue
    
    if s1_streams:
        print(f"\nFound {len(s1_streams)} S1 streams:")
        for key, info in sorted(s1_streams.items()):
            print(f"\n  {key}:")
            print(f"    Current State: {info['state']}")
            print(f"    Last Event: {info['event']}")
            print(f"    Last Event Time: {info['ts'].strftime('%Y-%m-%d %H:%M:%S UTC')}")
            
            # Check if should be in RANGE_BUILDING
            if time_diff > 0:
                if info['state'] not in ['RANGE_BUILDING', 'RANGE_LOCKED', 'DONE']:
                    print(f"    [WARNING] Should be in RANGE_BUILDING (range start was {time_diff:.1f}h ago)")
    else:
        print("\nNo S1 streams found in logs")
    
    # Check for RANGE_WINDOW_STARTED events
    print("\n" + "=" * 80)
    print("RANGE_WINDOW_STARTED EVENTS (Transition to RANGE_BUILDING)")
    print("=" * 80)
    
    range_started_events = []
    for log_file in log_files:
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        entry = json.loads(line)
                        if entry.get('event') == 'RANGE_WINDOW_STARTED':
                            data = entry.get('data', {})
                            ts_str = entry.get('ts_utc', '')
                            if ts_str:
                                if ts_str.endswith('Z'):
                                    ts_str = ts_str[:-1] + '+00:00'
                                try:
                                    ts = datetime.fromisoformat(ts_str)
                                    if ts >= now_utc.replace(hour=0, minute=0, second=0):
                                        range_started_events.append({
                                            'ts': ts,
                                            'instrument': data.get('instrument', ''),
                                            'stream': data.get('stream', ''),
                                            'session': data.get('session', '')
                                        })
                                except:
                                    pass
                    except:
                        continue
        except:
            continue
    
    if range_started_events:
        print(f"\nFound {len(range_started_events)} RANGE_WINDOW_STARTED events today:")
        for e in sorted(range_started_events, key=lambda x: x['ts']):
            if e['session'] == 'S1':
                print(f"  [{e['ts'].strftime('%H:%M:%S')}] {e['instrument']} {e['stream']} (S1)")
    else:
        print("\n[WARNING] No RANGE_WINDOW_STARTED events found today")
        print("          This means streams haven't transitioned to RANGE_BUILDING")

if __name__ == '__main__':
    main()
