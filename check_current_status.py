#!/usr/bin/env python3
"""Check current robot status and if S1 should be in RANGE_BUILDING right now"""
import json
import glob
from datetime import datetime, timezone
import pytz

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("CURRENT STATUS CHECK")
    print("=" * 80)
    
    # Get current time
    now_utc = datetime.now(timezone.utc)
    chicago_tz = pytz.timezone('America/Chicago')
    now_chicago = now_utc.astimezone(chicago_tz)
    
    print(f"\nCurrent Time:")
    print(f"  UTC: {now_utc.strftime('%Y-%m-%d %H:%M:%S')}")
    print(f"  Chicago: {now_chicago.strftime('%Y-%m-%d %H:%M:%S')}")
    
    # Load config to get S1 range start time
    try:
        with open('configs/analyzer_robot_parity.json', 'r') as f:
            config = json.load(f)
        
        s1_range_start_str = config.get('sessions', {}).get('S1', {}).get('range_start_time', '02:00')
        print(f"\nS1 Range Start Time (Chicago): {s1_range_start_str}")
        
        # Parse range start time
        range_hour, range_minute = map(int, s1_range_start_str.split(':'))
        today_chicago = now_chicago.replace(hour=range_hour, minute=range_minute, second=0, microsecond=0)
        
        # Convert to UTC for comparison
        range_start_utc = today_chicago.astimezone(timezone.utc).replace(tzinfo=timezone.utc)
        
        print(f"  Range Start UTC: {range_start_utc.strftime('%Y-%m-%d %H:%M:%S')}")
        
        # Check if we're past range start
        if now_utc >= range_start_utc:
            minutes_past = (now_utc - range_start_utc).total_seconds() / 60
            print(f"\n[STATUS] Range start was {minutes_past:.1f} minutes ago")
            print(f"         S1 streams SHOULD be in RANGE_BUILDING or RANGE_LOCKED")
        else:
            minutes_until = (range_start_utc - now_utc).total_seconds() / 60
            print(f"\n[STATUS] Range start is in {minutes_until:.1f} minutes")
            print(f"         S1 streams SHOULD be in ARMED state")
    except Exception as e:
        print(f"\n[ERROR] Could not load config: {e}")
    
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
    
    # Get events from last 10 minutes
    ten_minutes_ago = now_utc - timedelta(minutes=10)
    recent_events = [(ts, e) for ts, e in today_events if ts >= ten_minutes_ago]
    
    print(f"\n" + "=" * 80)
    print(f"RECENT ACTIVITY (Last 10 minutes)")
    print("=" * 80)
    print(f"Found {len(recent_events)} events in last 10 minutes")
    
    if len(recent_events) == 0:
        print("\n[CRITICAL] No events in last 10 minutes!")
        print("           Robot is NOT running or Tick() is not being called")
    else:
        print("\nLast 10 events:")
        for ts, entry in recent_events[-10:]:
            event = entry.get('event', '')
            state = entry.get('state', '')
            data = entry.get('data', {})
            instrument = data.get('instrument', '')
            session = data.get('session', '')
            
            time_diff = (now_utc - ts).total_seconds()
            print(f"  [{ts.strftime('%H:%M:%S')}] ({time_diff:.0f}s ago) {event} | State: {state} | {instrument} {session}")
    
    # Check for S1 streams and their current state
    print(f"\n" + "=" * 80)
    print(f"S1 STREAM STATUS")
    print("=" * 80)
    
    s1_events = [(ts, e) for ts, e in today_events if e.get('data', {}).get('session') == 'S1']
    
    if not s1_events:
        print("\n[WARNING] No S1 events found today")
    else:
        # Group by stream
        streams = {}
        for ts, entry in s1_events:
            data = entry.get('data', {})
            stream = data.get('stream', 'UNKNOWN')
            if stream not in streams:
                streams[stream] = []
            streams[stream].append((ts, entry))
        
        print(f"\nFound {len(streams)} S1 streams")
        
        for stream_id, events in sorted(streams.items()):
            events.sort(key=lambda x: x[0])
            latest_ts, latest_entry = events[-1]
            
            latest_state = latest_entry.get('state', '')
            latest_event = latest_entry.get('event', '')
            
            time_since_last = (now_utc - latest_ts).total_seconds() / 60
            
            print(f"\n  Stream: {stream_id}")
            print(f"    Latest State: {latest_state}")
            print(f"    Latest Event: {latest_event}")
            print(f"    Last Event Time: {latest_ts.strftime('%H:%M:%S')} UTC ({time_since_last:.1f} minutes ago)")
            
            if time_since_last > 5:
                print(f"    [WARNING] No activity for {time_since_last:.1f} minutes - robot may be stopped")
            
            # Check if it should be in RANGE_BUILDING
            if now_utc >= range_start_utc:
                if latest_state != 'RANGE_BUILDING' and latest_state != 'RANGE_LOCKED':
                    print(f"    [ISSUE] Should be in RANGE_BUILDING/RANGE_LOCKED but state is: {latest_state}")
    
    # Check for RANGE_WINDOW_STARTED today
    range_started_today = [(ts, e) for ts, e in today_events if e.get('event') == 'RANGE_WINDOW_STARTED']
    if range_started_today:
        print(f"\n[OK] Found {len(range_started_today)} RANGE_WINDOW_STARTED events today")
        for ts, entry in range_started_today[-3:]:
            data = entry.get('data', {})
            print(f"  [{ts.strftime('%H:%M:%S')}] {data.get('instrument', '')} {data.get('stream', '')} {data.get('session', '')}")
    else:
        print(f"\n[WARNING] No RANGE_WINDOW_STARTED events found today")
        if now_utc >= range_start_utc:
            print(f"         This means streams haven't transitioned to RANGE_BUILDING yet")

if __name__ == '__main__':
    from datetime import timedelta
    main()
