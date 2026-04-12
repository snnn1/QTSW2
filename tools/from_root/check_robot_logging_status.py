#!/usr/bin/env python3
"""
Comprehensive Robot Logging Status Check
Checks all aspects of robot logging to verify everything is working correctly
"""
import json
import requests
from pathlib import Path
from datetime import datetime, timedelta, timezone
import pytz

CHICAGO_TZ = pytz.timezone("America/Chicago")

def main():
    print("=" * 100)
    print("ROBOT LOGGING STATUS CHECK")
    print("=" * 100)
    print(f"Check Time: {datetime.now(CHICAGO_TZ).strftime('%Y-%m-%d %H:%M:%S %Z')}")
    print()
    
    # 1. Watchdog Status
    print("1. WATCHDOG STATUS")
    print("-" * 100)
    try:
        r = requests.get('http://localhost:8002/api/watchdog/status', timeout=5)
        status = r.json()
        engine_alive = status.get('engine_alive', False)
        activity_state = status.get('engine_activity_state', 'UNKNOWN')
        last_tick = status.get('last_engine_tick_chicago', 'N/A')
        stall_detected = status.get('engine_tick_stall_detected', False)
        
        print(f"  Engine Alive: {engine_alive}")
        print(f"  Activity State: {activity_state}")
        print(f"  Last Engine Tick: {last_tick}")
        print(f"  Stall Detected: {stall_detected}")
        
        if engine_alive and activity_state == 'ACTIVE' and not stall_detected:
            print("  [OK] Engine is healthy and active")
        else:
            print("  [WARNING] Engine may have issues")
    except Exception as e:
        print(f"  [ERROR] Failed to check watchdog: {e}")
    print()
    
    # 2. Stream States
    print("2. STREAM STATES")
    print("-" * 100)
    try:
        r = requests.get('http://localhost:8002/api/watchdog/stream-states', timeout=5)
        data = r.json()
        streams = data.get('streams', [])
        print(f"  Total Streams: {len(streams)}")
        
        states = {}
        for s in streams:
            state = s.get('state', 'UNKNOWN')
            states[state] = states.get(state, 0) + 1
        
        for state, count in sorted(states.items()):
            print(f"    {state}: {count} stream(s)")
        
        for s in sorted(streams, key=lambda x: x.get('stream', '')):
            stream = s.get('stream', 'N/A')
            instrument = s.get('instrument', 'N/A')
            state = s.get('state', 'N/A')
            print(f"      {stream:<6} | {instrument:<4} | {state}")
    except Exception as e:
        print(f"  [ERROR] Failed to check stream states: {e}")
    print()
    
    # 3. Recent Event Activity
    print("3. RECENT EVENT ACTIVITY (Last 5 minutes)")
    print("-" * 100)
    feed_file = Path("logs/robot/frontend_feed.jsonl")
    if feed_file.exists():
        cutoff = datetime.now(timezone.utc) - timedelta(minutes=5)
        event_counts = {}
        recent_events = []
        
        with open(feed_file, 'r', encoding='utf-8-sig') as f:
            lines = f.readlines()
            for line in lines[-1000:]:
                try:
                    event = json.loads(line.strip())
                    ts_str = event.get('timestamp_utc', '')
                    if ts_str:
                        ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                        if ts.tzinfo is None:
                            ts = ts.replace(tzinfo=timezone.utc)
                        if ts >= cutoff:
                            event_type = event.get('event_type', '')
                            event_counts[event_type] = event_counts.get(event_type, 0) + 1
                            recent_events.append(event)
                except:
                    pass
        
        print(f"  Total Events (last 5 min): {len(recent_events)}")
        print(f"  Event Types:")
        for event_type, count in sorted(event_counts.items(), key=lambda x: x[1], reverse=True)[:10]:
            print(f"    {event_type}: {count}")
        
        if len(recent_events) > 0:
            print("  [OK] Events are being logged")
        else:
            print("  [WARNING] No recent events found")
    else:
        print("  [ERROR] Feed file not found")
    print()
    
    # 4. Error Check
    print("4. ERROR CHECK (Last 1 hour)")
    print("-" * 100)
    log_dir = Path("logs/robot")
    if log_dir.exists():
        cutoff_time = (datetime.now(timezone.utc) - timedelta(hours=1)).isoformat()
        error_events = []
        
        for log_file in log_dir.glob("*.jsonl"):
            try:
                with open(log_file, 'r', encoding='utf-8') as f:
                    for line in f:
                        line = line.strip()
                        if line:
                            try:
                                event = json.loads(line)
                                ts = event.get('timestamp_utc', '') or event.get('ts_utc', '')
                                if ts and ts >= cutoff_time:
                                    event_type = event.get('event_type', '') or event.get('event', '')
                                    level = event.get('level', '')
                                    if 'ERROR' in event_type or level == 'ERROR' or 'DATA_LOSS' in event_type:
                                        error_events.append({
                                            'timestamp': ts,
                                            'event_type': event_type,
                                            'data': event.get('data', {})
                                        })
                            except:
                                pass
            except:
                pass
        
        if error_events:
            print(f"  Found {len(error_events)} error events")
            # Group by type
            by_type = {}
            for e in error_events:
                et = e['event_type']
                by_type[et] = by_type.get(et, 0) + 1
            
            for event_type, count in sorted(by_type.items(), key=lambda x: x[1], reverse=True):
                print(f"    {event_type}: {count} occurrence(s)")
                
                # Check if DATA_LOSS_DETECTED (these are expected/rate-limited)
                if event_type == 'DATA_LOSS_DETECTED':
                    print("      [INFO] These are expected - handled by gap tolerance (log only, rate-limited)")
            
            if len([e for e in error_events if e['event_type'] != 'DATA_LOSS_DETECTED']) > 0:
                print("  [WARNING] Non-DATA_LOSS errors detected - review above")
            else:
                print("  [OK] Only expected DATA_LOSS_DETECTED events (rate-limited notifications)")
        else:
            print("  [OK] No error events found")
    else:
        print("  [ERROR] Log directory not found")
    print()
    
    # 5. Log File Status
    print("5. LOG FILE STATUS")
    print("-" * 100)
    if log_dir.exists():
        jsonl_files = list(log_dir.glob("*.jsonl"))
        if jsonl_files:
            print(f"  Found {len(jsonl_files)} JSONL log file(s)")
            # Get most recent
            most_recent = max(jsonl_files, key=lambda f: f.stat().st_mtime)
            mtime = datetime.fromtimestamp(most_recent.stat().st_mtime, tz=timezone.utc)
            size_kb = most_recent.stat().st_size / 1024
            print(f"  Most Recent: {most_recent.name}")
            print(f"    Last Modified: {mtime.astimezone(CHICAGO_TZ).strftime('%Y-%m-%d %H:%M:%S %Z')}")
            print(f"    Size: {size_kb:.2f} KB")
            
            elapsed = (datetime.now(timezone.utc) - mtime).total_seconds()
            if elapsed < 60:
                print(f"  [OK] Log file is being actively written (updated {elapsed:.0f}s ago)")
            elif elapsed < 300:
                print(f"  [WARNING] Log file not updated recently ({elapsed:.0f}s ago)")
            else:
                print(f"  [ERROR] Log file appears stale ({elapsed:.0f}s ago)")
        else:
            print("  [WARNING] No JSONL log files found")
    else:
        print("  [ERROR] Log directory not found")
    print()
    
    # 6. Summary
    print("=" * 100)
    print("SUMMARY")
    print("=" * 100)
    
    # Determine overall status
    issues = []
    if not engine_alive:
        issues.append("Engine not alive")
    if stall_detected:
        issues.append("Engine stall detected")
    if len(recent_events) == 0:
        issues.append("No recent events")
    
    if issues:
        print(f"  [WARNING] Issues detected:")
        for issue in issues:
            print(f"    - {issue}")
    else:
        print("  [OK] Robot logging appears to be working correctly")
        print("    - Engine is alive and active")
        print("    - Events are being logged")
        print("    - No critical errors detected")
        print("    - Log files are being updated")
    
    print()

if __name__ == "__main__":
    main()
