"""Check why engine is showing as stalled"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def main():
    log_file = Path("logs/robot/robot_ENGINE.jsonl")
    
    events = []
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    events.append(json.loads(line))
                except:
                    pass
    
    # Get recent events (last 500)
    recent = events[-500:] if len(events) > 500 else events
    
    # Find ENGINE_TICK_HEARTBEAT events
    heartbeats = [e for e in recent if e.get('event') == 'ENGINE_TICK_HEARTBEAT']
    
    print("="*80)
    print("ENGINE STALL DIAGNOSIS")
    print("="*80)
    
    now_utc = datetime.now(timezone.utc)
    print(f"\nCurrent time (UTC): {now_utc.strftime('%Y-%m-%d %H:%M:%S UTC')}")
    
    print(f"\n[ENGINE_TICK_HEARTBEAT EVENTS]")
    print(f"  Total in recent logs: {len(heartbeats)}")
    
    if heartbeats:
        latest_heartbeat = heartbeats[-1]
        heartbeat_time = parse_timestamp(latest_heartbeat.get('ts_utc', ''))
        
        if heartbeat_time:
            seconds_ago = (now_utc - heartbeat_time.replace(tzinfo=timezone.utc)).total_seconds()
            print(f"\n  Latest heartbeat:")
            print(f"    Time: {heartbeat_time.strftime('%Y-%m-%d %H:%M:%S UTC')}")
            print(f"    Seconds ago: {seconds_ago:.1f}")
            print(f"    Threshold: 120 seconds")
            
            if seconds_ago > 120:
                print(f"\n  [STALLED] Last heartbeat was {seconds_ago:.1f} seconds ago (> 120s threshold)")
                print(f"    This is why watchdog shows ENGINE_STALLED")
            else:
                print(f"\n  [OK] Last heartbeat was {seconds_ago:.1f} seconds ago (< 120s threshold)")
                print(f"    Watchdog should show ACTIVE, not STALLED")
                print(f"    Check if watchdog is reading logs correctly")
        
        # Show recent heartbeats
        print(f"\n  Recent heartbeats (last 10):")
        for hb in heartbeats[-10:]:
            ts = parse_timestamp(hb.get('ts_utc', ''))
            if ts:
                ago = (now_utc - ts.replace(tzinfo=timezone.utc)).total_seconds()
                print(f"    {ts.strftime('%H:%M:%S UTC')} ({ago:.1f}s ago)")
    else:
        print(f"\n  [CRITICAL] No ENGINE_TICK_HEARTBEAT events found!")
        print(f"    This means:")
        print(f"      1. Engine is not emitting heartbeats")
        print(f"      2. Or heartbeats are not being logged")
        print(f"      3. Or logs are not being read correctly")
    
    # Check for ENGINE_START
    starts = [e for e in recent if e.get('event') == 'ENGINE_START']
    if starts:
        latest_start = starts[-1]
        start_time = parse_timestamp(latest_start.get('ts_utc', ''))
        if start_time:
            seconds_since_start = (now_utc - start_time.replace(tzinfo=timezone.utc)).total_seconds()
            print(f"\n[ENGINE_START]")
            print(f"  Latest start: {start_time.strftime('%Y-%m-%d %H:%M:%S UTC')}")
            print(f"  Seconds since start: {seconds_since_start:.1f}")
    
    # Check for any recent activity
    recent_activity = [e for e in recent[-50:] 
                      if parse_timestamp(e.get('ts_utc', '')) and
                      (now_utc - parse_timestamp(e.get('ts_utc', '')).replace(tzinfo=timezone.utc)).total_seconds() < 300]
    
    print(f"\n[RECENT ACTIVITY]")
    print(f"  Events in last 5 minutes: {len(recent_activity)}")
    
    if recent_activity:
        event_types = {}
        for e in recent_activity:
            event_type = e.get('event', 'UNKNOWN')
            event_types[event_type] = event_types.get(event_type, 0) + 1
        
        print(f"  Event breakdown:")
        for event_type, count in sorted(event_types.items(), key=lambda x: x[1], reverse=True)[:10]:
            print(f"    {event_type}: {count}")
    
    # Summary
    print("\n" + "="*80)
    print("DIAGNOSIS")
    print("="*80)
    
    if not heartbeats:
        print("\n[ISSUE] No ENGINE_TICK_HEARTBEAT events found")
        print("  Possible causes:")
        print("    1. Engine Tick() method not being called")
        print("    2. Heartbeat logging disabled or filtered")
        print("    3. Logs not being written/read correctly")
    elif heartbeats:
        latest = heartbeats[-1]
        latest_time = parse_timestamp(latest.get('ts_utc', ''))
        if latest_time:
            seconds_ago = (now_utc - latest_time.replace(tzinfo=timezone.utc)).total_seconds()
            if seconds_ago > 120:
                print(f"\n[ISSUE] Last heartbeat was {seconds_ago:.1f} seconds ago")
                print(f"  Watchdog threshold: 120 seconds")
                print(f"  Engine appears to have stopped emitting heartbeats")
                print(f"  Check if:")
                print(f"    1. Engine Tick() timer stopped")
                print(f"    2. Engine crashed or stopped")
                print(f"    3. Logging service stopped")
            else:
                print(f"\n[OK] Engine is emitting heartbeats")
                print(f"  Last heartbeat: {seconds_ago:.1f} seconds ago")
                print(f"  If watchdog still shows STALLED, check:")
                print(f"    1. Watchdog is reading correct log file")
                print(f"    2. Watchdog cursor is up to date")
                print(f"    3. Market status detection is correct")

if __name__ == '__main__':
    main()
