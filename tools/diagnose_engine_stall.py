"""Diagnose why engine shows as stalled"""
import json
from pathlib import Path
from datetime import datetime, timezone

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
    
    recent = events[-200:] if len(events) > 200 else events
    
    print("="*80)
    print("ENGINE STALL DIAGNOSIS")
    print("="*80)
    
    now_utc = datetime.now(timezone.utc)
    print(f"\nCurrent time (UTC): {now_utc.strftime('%Y-%m-%d %H:%M:%S UTC')}")
    
    # Find ENGINE_START
    starts = [e for e in recent if e.get('event') == 'ENGINE_START']
    if starts:
        latest_start = starts[-1]
        start_time = parse_timestamp(latest_start.get('ts_utc', ''))
        if start_time:
            seconds_ago = (now_utc - start_time.replace(tzinfo=timezone.utc)).total_seconds()
            print(f"\n[ENGINE_START]")
            print(f"  Time: {start_time.strftime('%Y-%m-%d %H:%M:%S UTC')}")
            print(f"  Seconds ago: {seconds_ago:.1f}")
    
    # Check for ENGINE_TICK_HEARTBEAT
    heartbeats = [e for e in recent if e.get('event') == 'ENGINE_TICK_HEARTBEAT']
    print(f"\n[ENGINE_TICK_HEARTBEAT]")
    print(f"  Found: {len(heartbeats)} events")
    
    if heartbeats:
        latest = heartbeats[-1]
        hb_time = parse_timestamp(latest.get('ts_utc', ''))
        if hb_time:
            seconds_ago = (now_utc - hb_time.replace(tzinfo=timezone.utc)).total_seconds()
            print(f"  Latest: {hb_time.strftime('%Y-%m-%d %H:%M:%S UTC')} ({seconds_ago:.1f}s ago)")
            if seconds_ago > 120:
                print(f"  [STALLED] Last heartbeat > 120s ago")
            else:
                print(f"  [OK] Last heartbeat < 120s ago")
    else:
        print(f"  [CRITICAL] No heartbeats found!")
        print(f"    This means Tick() is not being called or heartbeats aren't being logged")
    
    # Check for any recent activity
    very_recent = [e for e in recent 
                  if parse_timestamp(e.get('ts_utc', '')) and
                  (now_utc - parse_timestamp(e.get('ts_utc', '')).replace(tzinfo=timezone.utc)).total_seconds() < 180]
    
    print(f"\n[RECENT ACTIVITY - Last 3 minutes]")
    print(f"  Events: {len(very_recent)}")
    
    if very_recent:
        event_types = {}
        for e in very_recent:
            et = e.get('event', 'UNKNOWN')
            event_types[et] = event_types.get(et, 0) + 1
        
        print(f"  Top events:")
        for et, count in sorted(event_types.items(), key=lambda x: x[1], reverse=True)[:10]:
            print(f"    {et}: {count}")
    
    # Summary
    print("\n" + "="*80)
    print("ROOT CAUSE")
    print("="*80)
    
    if not heartbeats:
        print("\n[ISSUE] No ENGINE_TICK_HEARTBEAT events found")
        print("  This means Tick() is not being called by the NinjaTrader timer")
        print("\n  Possible causes:")
        print("    1. Timer not started - check if strategy reached DataLoaded/Realtime state")
        print("    2. Timer callback failing silently - check NinjaTrader logs for errors")
        print("    3. _engineReady flag is false - timer won't call Tick() if engine not ready")
        print("    4. Engine is null - timer callback returns early")
        print("\n  Check NinjaTrader Output window for:")
        print("    - 'Tick timer started (1 second interval)' message")
        print("    - 'ERROR in tick timer callback' messages")
        print("    - 'Engine ready - all initialization complete' message")
    elif heartbeats:
        latest = heartbeats[-1]
        hb_time = parse_timestamp(latest.get('ts_utc', ''))
        if hb_time:
            seconds_ago = (now_utc - hb_time.replace(tzinfo=timezone.utc)).total_seconds()
            if seconds_ago > 120:
                print(f"\n[ISSUE] Last heartbeat was {seconds_ago:.1f} seconds ago")
                print(f"  Watchdog threshold: 120 seconds")
                print(f"  Engine appears to have stopped emitting heartbeats")
                print(f"  Check if Tick() timer stopped or engine crashed")

if __name__ == '__main__':
    main()
